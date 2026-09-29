using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => {
    o.Cookie.HttpOnly = true; o.Cookie.IsEssential = true; o.Cookie.SameSite = SameSiteMode.Lax; o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseDefaultFiles(); app.UseStaticFiles(); app.UseAuthentication(); app.UseAuthorization();

var dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? "/app/data";
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "petcare.db");
string Cs() => $"Data Source={dbPath}";
using (var cn = new SqliteConnection(Cs())) { cn.Open(); var cmd = cn.CreateCommand(); cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Users(Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT UNIQUE NOT NULL, PasswordHash TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Pets(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Age INTEGER, Breed TEXT, Gender TEXT, Weight REAL);
CREATE TABLE IF NOT EXISTS Customers(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Phone TEXT, Address TEXT);
CREATE TABLE IF NOT EXISTS Washes(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Price REAL, Time TEXT);
CREATE TABLE IF NOT EXISTS Boardings(Id TEXT PRIMARY KEY, PetId TEXT, StartDate TEXT, EndDate TEXT, PricePerDay REAL, Status TEXT);
CREATE TABLE IF NOT EXISTS Orders(Id TEXT PRIMARY KEY, PetId TEXT, ServiceType TEXT, Price REAL, CreateTime TEXT, Status TEXT);"; cmd.ExecuteNonQuery(); }

string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
SqliteConnection Open(){ var c = new SqliteConnection(Cs()); c.Open(); return c; }
object? Scalar(string sql, params (string,object?)[] p){ using var c=Open(); using var m=c.CreateCommand(); m.CommandText=sql; foreach(var x in p)m.Parameters.AddWithValue(x.Item1,x.Item2??DBNull.Value); return m.ExecuteScalar(); }
int Exec(string sql, params (string,object?)[] p){ using var c=Open(); using var m=c.CreateCommand(); m.CommandText=sql; foreach(var x in p)m.Parameters.AddWithValue(x.Item1,x.Item2??DBNull.Value); return m.ExecuteNonQuery(); }
List<Dictionary<string,object?>> Query(string sql){ using var c=Open(); using var m=c.CreateCommand(); m.CommandText=sql; using var r=m.ExecuteReader(); var list=new List<Dictionary<string,object?>>(); while(r.Read()){ var d=new Dictionary<string,object?>(); for(int i=0;i<r.FieldCount;i++)d[r.GetName(i)] = r.IsDBNull(i)?null:r.GetValue(i); list.Add(d);} return list; }

app.MapPost("/api/auth/register", async (HttpContext ctx, LoginDto dto) => { if(string.IsNullOrWhiteSpace(dto.Name)||dto.Password?.Length<4)return Results.BadRequest("用户名不能为空，密码至少4位"); try{ Exec("INSERT INTO Users(Name,PasswordHash) VALUES($n,$p)",( "$n",dto.Name.Trim()),("$p",Hash(dto.Password))); return Results.Ok(); }catch{return Results.BadRequest("用户名已存在");} });
app.MapPost("/api/auth/login", async (HttpContext ctx, LoginDto dto) => { var ph=Scalar("SELECT PasswordHash FROM Users WHERE Name=$n",("$n",dto.Name?.Trim())); if(ph is null || !string.Equals(ph.ToString(),Hash(dto.Password??""),StringComparison.OrdinalIgnoreCase))return Results.Unauthorized(); var id=new ClaimsIdentity(new[]{new Claim(ClaimTypes.Name,dto.Name!.Trim())},CookieAuthenticationDefaults.AuthenticationScheme); await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,new ClaimsPrincipal(id)); return Results.Ok(new{name=dto.Name}); });
app.MapPost("/api/auth/logout", [Authorize] async (HttpContext ctx)=>{await ctx.SignOutAsync(); return Results.Ok();});
app.MapGet("/api/auth/me", [Authorize] (ClaimsPrincipal u)=>Results.Ok(new{name=u.Identity?.Name??"管理员"}));

void MapCrud(string route,string table,string select,string insert,string update, Func<Dictionary<string,object?>,object> normalize){
 app.MapGet(route,[Authorize] ()=>Results.Ok(Query(select).Select(normalize)));
 app.MapPost(route,[Authorize] async (HttpContext ctx)=>{var d=await ctx.Request.ReadFromJsonAsync<Dictionary<string,object?>>(); if(d is null)return Results.BadRequest(); try{ using var c=Open(); using var m=c.CreateCommand(); m.CommandText=insert; foreach(var kv in d)m.Parameters.AddWithValue("$"+kv.Key,kv.Value?.ToString()??""); m.ExecuteNonQuery(); return Results.Ok();}catch(Exception e){return Results.BadRequest(e.Message.Contains("UNIQUE")||e.Message.Contains("PRIMARY")?"编号已存在":"保存失败");}});
 app.MapPut(route+"/{id}",[Authorize] async (string id,HttpContext ctx)=>{var d=await ctx.Request.ReadFromJsonAsync<Dictionary<string,object?>>(); if(d is null)return Results.BadRequest(); using var c=Open(); using var m=c.CreateCommand(); m.CommandText=update; m.Parameters.AddWithValue("$id",id); foreach(var kv in d)m.Parameters.AddWithValue("$"+kv.Key,kv.Value?.ToString()??""); return m.ExecuteNonQuery()>0?Results.Ok():Results.NotFound();});
 app.MapDelete(route+"/{id}",[Authorize] (string id)=>Exec($"DELETE FROM {table} WHERE Id=$id",("$id",id))>0?Results.Ok():Results.NotFound());
}
object N(Dictionary<string,object?> d)=>d;
MapCrud("/api/pets","Pets","SELECT Id as id,Name as name,Age as age,Breed as breed,Gender as gender,Weight as weight FROM Pets ORDER BY Name",
"INSERT INTO Pets(Id,Name,Age,Breed,Gender,Weight) VALUES($id,$name,$age,$breed,$gender,$weight)",
"UPDATE Pets SET Name=$name,Age=$age,Breed=$breed,Gender=$gender,Weight=$weight WHERE Id=$id",N);
MapCrud("/api/customers","Customers","SELECT Id as id,Name as name,Phone as phone,Address as address FROM Customers ORDER BY Name",
"INSERT INTO Customers(Id,Name,Phone,Address) VALUES($id,$name,$phone,$address)",
"UPDATE Customers SET Name=$name,Phone=$phone,Address=$address WHERE Id=$id",N);
MapCrud("/api/washes","Washes","SELECT Id as id,Name as name,Price as price,Time as time FROM Washes ORDER BY Name",
"INSERT INTO Washes(Id,Name,Price,Time) VALUES($id,$name,$price,$time)",
"UPDATE Washes SET Name=$name,Price=$price,Time=$time WHERE Id=$id",N);
MapCrud("/api/boardings","Boardings","SELECT Id as id,PetId as petId,StartDate as startDate,EndDate as endDate,PricePerDay as pricePerDay,Status as status FROM Boardings ORDER BY StartDate DESC",
"INSERT INTO Boardings(Id,PetId,StartDate,EndDate,PricePerDay,Status) VALUES($id,$petId,$startDate,$endDate,$pricePerDay,$status)",
"UPDATE Boardings SET PetId=$petId,StartDate=$startDate,EndDate=$endDate,PricePerDay=$pricePerDay,Status=$status WHERE Id=$id",N);
MapCrud("/api/orders","Orders","SELECT Id as id,PetId as petId,ServiceType as serviceType,Price as price,CreateTime as createTime,Status as status FROM Orders ORDER BY CreateTime DESC",
"INSERT INTO Orders(Id,PetId,ServiceType,Price,CreateTime,Status) VALUES($id,$petId,$serviceType,$price,$createTime,$status)",
"UPDATE Orders SET PetId=$petId,ServiceType=$serviceType,Price=$price,CreateTime=$createTime,Status=$status WHERE Id=$id",N);

app.MapGet("/api/dashboard",[Authorize] ()=>Results.Ok(new{pets=Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Pets")),customers=Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Customers")),boardings=Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Boardings WHERE Status='寄养中'")),orders=Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Orders"))}));
app.MapGet("/health",()=>Results.Ok(new{status="ok"}));
app.MapFallbackToFile("index.html");
var port=Environment.GetEnvironmentVariable("PORT")??"8080"; app.Urls.Add($"http://0.0.0.0:{port}"); app.Run();
record LoginDto(string? Name,string? Password);
