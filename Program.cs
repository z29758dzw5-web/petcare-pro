using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.HttpOnly = true;
        o.Cookie.IsEssential = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? "/app/data";
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "petcare.db");
string Cs() => $"Data Source={dbPath}";

SqliteConnection Open()
{
    var c = new SqliteConnection(Cs());
    c.Open();
    return c;
}

object? Scalar(string sql, params (string, object?)[] p)
{
    using var c = Open();
    using var m = c.CreateCommand();
    m.CommandText = sql;
    foreach (var x in p) m.Parameters.AddWithValue(x.Item1, x.Item2 ?? DBNull.Value);
    return m.ExecuteScalar();
}

int Exec(string sql, params (string, object?)[] p)
{
    using var c = Open();
    using var m = c.CreateCommand();
    m.CommandText = sql;
    foreach (var x in p) m.Parameters.AddWithValue(x.Item1, x.Item2 ?? DBNull.Value);
    return m.ExecuteNonQuery();
}

List<Dictionary<string, object?>> Query(string sql)
{
    using var c = Open();
    using var m = c.CreateCommand();
    m.CommandText = sql;
    using var r = m.ExecuteReader();
    var list = new List<Dictionary<string, object?>>();
    while (r.Read())
    {
        var d = new Dictionary<string, object?>();
        for (int i = 0; i < r.FieldCount; i++)
            d[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        list.Add(d);
    }
    return list;
}

using (var cn = Open())
{
    using var cmd = cn.CreateCommand();
    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Users(Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT UNIQUE NOT NULL, PasswordHash TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS Pets(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Age INTEGER, Breed TEXT, Gender TEXT, Weight REAL);
CREATE TABLE IF NOT EXISTS Customers(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Phone TEXT, Address TEXT, PetId TEXT);
CREATE TABLE IF NOT EXISTS Washes(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Price REAL, Time TEXT, PetId TEXT);
CREATE TABLE IF NOT EXISTS Boardings(Id TEXT PRIMARY KEY, PetId TEXT, StartDate TEXT, EndDate TEXT, PricePerDay REAL, Status TEXT);
CREATE TABLE IF NOT EXISTS Orders(Id TEXT PRIMARY KEY, PetId TEXT, ServiceType TEXT, Price REAL, CreateTime TEXT, Status TEXT);";
    cmd.ExecuteNonQuery();
}

try { Exec("ALTER TABLE Washes ADD COLUMN PetId TEXT"); } catch { }
try { Exec("ALTER TABLE Customers ADD COLUMN PetId TEXT"); } catch { }

string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
string NewOrderId(string prefix) => $"{prefix}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..5]}";

app.MapPost("/api/auth/register", (LoginDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Password) || dto.Password.Length < 4)
        return Results.BadRequest("用户名不能为空，密码至少4位");
    try
    {
        Exec("INSERT INTO Users(Name,PasswordHash) VALUES($n,$p)", ("$n", dto.Name.Trim()), ("$p", Hash(dto.Password)));
        return Results.Ok();
    }
    catch { return Results.BadRequest("用户名已存在"); }
});

app.MapPost("/api/auth/login", async (HttpContext ctx, LoginDto dto) =>
{
    var ph = Scalar("SELECT PasswordHash FROM Users WHERE Name=$n", ("$n", dto.Name?.Trim()));
    if (ph is null || !string.Equals(ph.ToString(), Hash(dto.Password ?? ""), StringComparison.OrdinalIgnoreCase))
        return Results.Unauthorized();

    var identity = new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Name, dto.Name!.Trim()) },
        CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(new { name = dto.Name });
});

app.MapPost("/api/auth/logout", [Authorize] async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Ok();
});
app.MapGet("/api/auth/me", [Authorize] (ClaimsPrincipal u) => Results.Ok(new { name = u.Identity?.Name ?? "管理员" }));

app.MapGet("/api/pets", [Authorize] () => Results.Ok(Query("SELECT Id as id,Name as name,Age as age,Breed as breed,Gender as gender,Weight as weight FROM Pets ORDER BY Name")));
app.MapPost("/api/pets", [Authorize] (PetDto d) =>
{
    if (string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.Name)) return Results.BadRequest("宠物编号和名称不能为空");
    try
    {
        Exec("INSERT INTO Pets(Id,Name,Age,Breed,Gender,Weight) VALUES($id,$name,$age,$breed,$gender,$weight)",
            ("$id", d.Id.Trim()), ("$name", d.Name.Trim()), ("$age", d.Age), ("$breed", d.Breed), ("$gender", d.Gender), ("$weight", d.Weight));
        return Results.Ok();
    }
    catch { return Results.BadRequest("宠物编号已存在"); }
});
app.MapPut("/api/pets/{id}", [Authorize] (string id, PetDto d) =>
    Exec("UPDATE Pets SET Name=$name,Age=$age,Breed=$breed,Gender=$gender,Weight=$weight WHERE Id=$id",
        ("$id", id), ("$name", d.Name), ("$age", d.Age), ("$breed", d.Breed), ("$gender", d.Gender), ("$weight", d.Weight)) > 0 ? Results.Ok() : Results.NotFound());
app.MapDelete("/api/pets/{id}", [Authorize] (string id) =>
    Exec("DELETE FROM Pets WHERE Id=$id", ("$id", id)) > 0 ? Results.Ok() : Results.NotFound());

app.MapGet("/api/customers", [Authorize] () => Results.Ok(Query(@"
SELECT c.Id as id,c.Name as name,c.Phone as phone,c.Address as address,c.PetId as petId,
       COALESCE(p.Name,'') as petName,COALESCE(p.Breed,'') as petBreed
FROM Customers c LEFT JOIN Pets p ON p.Id=c.PetId ORDER BY c.Name")));
app.MapPost("/api/customers", [Authorize] (CustomerDto d) =>
{
    if (!string.IsNullOrWhiteSpace(d.PetId) && Scalar("SELECT 1 FROM Pets WHERE Id=$id", ("$id", d.PetId.Trim())) is null)
        return Results.BadRequest("宠物编号不存在");
    try
    {
        Exec("INSERT INTO Customers(Id,Name,Phone,Address,PetId) VALUES($id,$name,$phone,$address,$petId)",
            ("$id", d.Id), ("$name", d.Name), ("$phone", d.Phone), ("$address", d.Address), ("$petId", d.PetId?.Trim()));
        return Results.Ok();
    }
    catch { return Results.BadRequest("客户编号已存在"); }
});
app.MapPut("/api/customers/{id}", [Authorize] (string id, CustomerDto d) =>
{
    if (!string.IsNullOrWhiteSpace(d.PetId) && Scalar("SELECT 1 FROM Pets WHERE Id=$id", ("$id", d.PetId.Trim())) is null)
        return Results.BadRequest("宠物编号不存在");
    return Exec("UPDATE Customers SET Name=$name,Phone=$phone,Address=$address,PetId=$petId WHERE Id=$id",
        ("$id", id), ("$name", d.Name), ("$phone", d.Phone), ("$address", d.Address), ("$petId", d.PetId?.Trim())) > 0 ? Results.Ok() : Results.NotFound();
});
app.MapDelete("/api/customers/{id}", [Authorize] (string id) => Exec("DELETE FROM Customers WHERE Id=$id", ("$id", id)) > 0 ? Results.Ok() : Results.NotFound());

app.MapGet("/api/washes", [Authorize] () => Results.Ok(Query(@"
SELECT w.Id as id,w.PetId as petId,COALESCE(p.Name,'') as petName,w.Name as name,w.Price as price,w.Time as time
FROM Washes w LEFT JOIN Pets p ON p.Id=w.PetId ORDER BY w.Id DESC")));
app.MapPost("/api/washes", [Authorize] (WashDto d) =>
{
    if (string.IsNullOrWhiteSpace(d.PetId)) return Results.BadRequest("请选择宠物");
    try
    {
        Exec("INSERT INTO Washes(Id,Name,Price,Time,PetId) VALUES($id,$name,$price,$time,$petId)",
            ("$id", d.Id), ("$name", d.Name), ("$price", d.Price), ("$time", d.Time), ("$petId", d.PetId));
        Exec("INSERT INTO Orders(Id,PetId,ServiceType,Price,CreateTime,Status) VALUES($id,$petId,$type,$price,$time,$status)",
            ("$id", NewOrderId("WASH")), ("$petId", d.PetId), ("$type", $"洗护-{d.Name}"), ("$price", d.Price), ("$time", DateTime.Now.ToString("yyyy-MM-dd HH:mm")), ("$status", "待完成"));
        return Results.Ok();
    }
    catch { return Results.BadRequest("洗护编号已存在或保存失败"); }
});
app.MapPut("/api/washes/{id}", [Authorize] (string id, WashDto d) =>
    Exec("UPDATE Washes SET Name=$name,Price=$price,Time=$time,PetId=$petId WHERE Id=$id",
        ("$id", id), ("$name", d.Name), ("$price", d.Price), ("$time", d.Time), ("$petId", d.PetId)) > 0 ? Results.Ok() : Results.NotFound());
app.MapDelete("/api/washes/{id}", [Authorize] (string id) => Exec("DELETE FROM Washes WHERE Id=$id", ("$id", id)) > 0 ? Results.Ok() : Results.NotFound());

app.MapGet("/api/boardings", [Authorize] () => Results.Ok(Query(@"
SELECT b.Id as id,b.PetId as petId,COALESCE(p.Name,'') as petName,b.StartDate as startDate,b.EndDate as endDate,b.PricePerDay as pricePerDay,b.Status as status
FROM Boardings b LEFT JOIN Pets p ON p.Id=b.PetId ORDER BY b.StartDate DESC")));
app.MapPost("/api/boardings", [Authorize] (BoardingDto d) =>
{
    if (string.IsNullOrWhiteSpace(d.PetId)) return Results.BadRequest("请选择宠物");
    if (!DateTime.TryParse(d.StartDate, out var start) || !DateTime.TryParse(d.EndDate, out var end) || end <= start)
        return Results.BadRequest("结束时间必须晚于开始时间");
    try
    {
        Exec("INSERT INTO Boardings(Id,PetId,StartDate,EndDate,PricePerDay,Status) VALUES($id,$petId,$start,$end,$price,$status)",
            ("$id", d.Id), ("$petId", d.PetId), ("$start", d.StartDate), ("$end", d.EndDate), ("$price", d.PricePerDay), ("$status", d.Status));
        var days = Math.Max(1, (int)Math.Ceiling((end - start).TotalDays));
        var total = days * d.PricePerDay;
        Exec("INSERT INTO Orders(Id,PetId,ServiceType,Price,CreateTime,Status) VALUES($id,$petId,$type,$price,$time,$status)",
            ("$id", NewOrderId("BOARD")), ("$petId", d.PetId), ("$type", $"寄养-{days}天"), ("$price", total), ("$time", DateTime.Now.ToString("yyyy-MM-dd HH:mm")), ("$status", "进行中"));
        return Results.Ok(new { days, total });
    }
    catch { return Results.BadRequest("寄养编号已存在或保存失败"); }
});
app.MapPut("/api/boardings/{id}", [Authorize] (string id, BoardingDto d) =>
    Exec("UPDATE Boardings SET PetId=$petId,StartDate=$start,EndDate=$end,PricePerDay=$price,Status=$status WHERE Id=$id",
        ("$id", id), ("$petId", d.PetId), ("$start", d.StartDate), ("$end", d.EndDate), ("$price", d.PricePerDay), ("$status", d.Status)) > 0 ? Results.Ok() : Results.NotFound());
app.MapDelete("/api/boardings/{id}", [Authorize] (string id) => Exec("DELETE FROM Boardings WHERE Id=$id", ("$id", id)) > 0 ? Results.Ok() : Results.NotFound());

app.MapGet("/api/orders", [Authorize] () => Results.Ok(Query(@"
SELECT o.Id as id,o.PetId as petId,COALESCE(p.Name,'') as petName,o.ServiceType as serviceType,o.Price as price,o.CreateTime as createTime,o.Status as status
FROM Orders o LEFT JOIN Pets p ON p.Id=o.PetId ORDER BY o.CreateTime DESC")));
app.MapPost("/api/orders", [Authorize] (OrderDto d) =>
{
    try
    {
        Exec("INSERT INTO Orders(Id,PetId,ServiceType,Price,CreateTime,Status) VALUES($id,$petId,$type,$price,$time,$status)",
            ("$id", d.Id), ("$petId", d.PetId), ("$type", d.ServiceType), ("$price", d.Price), ("$time", d.CreateTime), ("$status", d.Status));
        return Results.Ok();
    }
    catch { return Results.BadRequest("订单编号已存在"); }
});
app.MapPut("/api/orders/{id}", [Authorize] (string id, OrderDto d) =>
    Exec("UPDATE Orders SET PetId=$petId,ServiceType=$type,Price=$price,CreateTime=$time,Status=$status WHERE Id=$id",
        ("$id", id), ("$petId", d.PetId), ("$type", d.ServiceType), ("$price", d.Price), ("$time", d.CreateTime), ("$status", d.Status)) > 0 ? Results.Ok() : Results.NotFound());
app.MapDelete("/api/orders/{id}", [Authorize] (string id) => Exec("DELETE FROM Orders WHERE Id=$id", ("$id", id)) > 0 ? Results.Ok() : Results.NotFound());

app.MapGet("/api/dashboard", [Authorize] () => Results.Ok(new
{
    pets = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Pets")),
    customers = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Customers")),
    boardings = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Boardings WHERE Status='寄养中' OR Status='进行中'")),
    orders = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Orders"))
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapFallbackToFile("index.html");

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://0.0.0.0:{port}");
app.Run();

record LoginDto(string? Name, string? Password);
record PetDto(string Id, string Name, int? Age, string? Breed, string? Gender, double? Weight);
record CustomerDto(string Id, string Name, string? Phone, string? Address, string? PetId);
record WashDto(string Id, string? PetId, string Name, double Price, string? Time);
record BoardingDto(string Id, string? PetId, string StartDate, string EndDate, double PricePerDay, string? Status);
record OrderDto(string Id, string? PetId, string? ServiceType, double Price, string? CreateTime, string? Status);
