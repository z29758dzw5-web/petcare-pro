using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

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

var dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(app.Environment.ContentRootPath, "data");
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
CREATE TABLE IF NOT EXISTS Users(Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT UNIQUE NOT NULL, PasswordHash TEXT NOT NULL, Phone TEXT);
CREATE TABLE IF NOT EXISTS Pets(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Age INTEGER, Breed TEXT, Gender TEXT, Weight REAL);
CREATE TABLE IF NOT EXISTS Customers(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Phone TEXT, Address TEXT, PetId TEXT);
CREATE TABLE IF NOT EXISTS Washes(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Price REAL, Time TEXT, PetId TEXT);
CREATE TABLE IF NOT EXISTS Boardings(Id TEXT PRIMARY KEY, PetId TEXT, StartDate TEXT, EndDate TEXT, PricePerDay REAL, Status TEXT);
CREATE TABLE IF NOT EXISTS Orders(Id TEXT PRIMARY KEY, PetId TEXT, ServiceType TEXT, Price REAL, CreateTime TEXT, Status TEXT);";
    cmd.ExecuteNonQuery();
}

try { Exec("ALTER TABLE Washes ADD COLUMN PetId TEXT"); } catch { }
try { Exec("ALTER TABLE Customers ADD COLUMN PetId TEXT"); } catch { }

try { Exec("ALTER TABLE Users ADD COLUMN Phone TEXT"); } catch { }
try { Exec("ALTER TABLE Orders ADD COLUMN PaymentStatus TEXT DEFAULT '待支付'"); } catch { }
try { Exec("ALTER TABLE Orders ADD COLUMN PaymentMethod TEXT"); } catch { }
try { Exec("ALTER TABLE Orders ADD COLUMN PaidAt TEXT"); } catch { }

Exec(@"CREATE TABLE IF NOT EXISTS Appointments(
    Id TEXT PRIMARY KEY,
    CustomerId TEXT,
    PetId TEXT,
    ServiceType TEXT NOT NULL,
    AppointmentTime TEXT NOT NULL,
    Staff TEXT,
    Status TEXT,
    Remark TEXT,
    CreatedAt TEXT NOT NULL
)");

Exec(@"CREATE TABLE IF NOT EXISTS Members(
    Id TEXT PRIMARY KEY,
    CustomerId TEXT,
    Level TEXT,
    Points INTEGER DEFAULT 0,
    Balance REAL DEFAULT 0,
    Status TEXT,
    JoinDate TEXT NOT NULL
)");

Exec(@"CREATE TABLE IF NOT EXISTS OperationLogs(
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Actor TEXT,
    Method TEXT,
    Path TEXT,
    StatusCode INTEGER,
    CreatedAt TEXT NOT NULL
)");

string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
bool ValidPhone(string? phone) => !string.IsNullOrWhiteSpace(phone) && Regex.IsMatch(phone.Trim(), @"^\+?[0-9]{8,15}$");
bool StrongPassword(string? password) => !string.IsNullOrWhiteSpace(password) && password.Length >= 8 && password.Any(char.IsLetter) && password.Any(char.IsDigit);
string NewOrderId(string prefix) => $"{prefix}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..5]}";
var smsCodes = new ConcurrentDictionary<string, SmsCodeInfo>();

app.Use(async (ctx, next) =>
{
    await next();
    var method = ctx.Request.Method;
    if (ctx.Request.Path.StartsWithSegments("/api") &&
        (method == "POST" || method == "PUT" || method == "DELETE" || method == "PATCH"))
    {
        try
        {
            Exec("INSERT INTO OperationLogs(Actor,Method,Path,StatusCode,CreatedAt) VALUES($a,$m,$p,$s,$t)",
                ("$a", ctx.User.Identity?.Name ?? "匿名"),
                ("$m", method),
                ("$p", ctx.Request.Path.Value ?? ""),
                ("$s", ctx.Response.StatusCode),
                ("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
        }
        catch { }
    }
});

app.MapPost("/api/auth/register", (LoginDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Name))
        return Results.BadRequest("请输入用户名");
    if (!StrongPassword(dto.Password))
        return Results.BadRequest("密码至少8位，并且必须同时包含字母和数字");
    if (!ValidPhone(dto.Phone))
        return Results.BadRequest("请输入8-15位有效手机号，可带+号");
    if (Scalar("SELECT 1 FROM Users WHERE Phone=$phone", ("$phone", dto.Phone.Trim())) is not null)
        return Results.BadRequest("手机号已经注册");
    try
    {
        Exec("INSERT INTO Users(Name,PasswordHash,Phone) VALUES($n,$p,$phone)",
            ("$n", dto.Name.Trim()), ("$p", Hash(dto.Password)), ("$phone", dto.Phone.Trim()));
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

app.MapPost("/api/auth/send-code", (SmsSendDto dto) =>
{
    if (!ValidPhone(dto.Phone)) return Results.BadRequest("请输入8-15位有效手机号，可带+号");
    var phone = dto.Phone!.Trim();
    if (Scalar("SELECT 1 FROM Users WHERE Phone=$phone", ("$phone", phone)) is null)
        return Results.BadRequest("这个手机号还没有注册");

    var code = Random.Shared.Next(100000, 999999).ToString();
    smsCodes[phone] = new SmsCodeInfo(code, DateTime.UtcNow.AddMinutes(5));
    Console.WriteLine($"短信验证码 {phone}: {code}");

    // DEMO：直接返回验证码，方便任何设备测试；接真实短信平台时删除 demoCode。
    return Results.Ok(new { message = "验证码已生成（演示模式）", demoCode = code });
});

app.MapPost("/api/auth/sms-login", async (HttpContext ctx, SmsLoginDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Phone) || string.IsNullOrWhiteSpace(dto.Code))
        return Results.BadRequest("手机号和验证码不能为空");
    var phone = dto.Phone.Trim();
    if (!smsCodes.TryGetValue(phone, out var info))
        return Results.BadRequest("请先获取验证码");
    if (DateTime.UtcNow > info.ExpireTime)
    {
        smsCodes.TryRemove(phone, out _);
        return Results.BadRequest("验证码已过期");
    }
    if (info.Code != dto.Code.Trim())
        return Results.BadRequest("验证码错误");

    var username = Scalar("SELECT Name FROM Users WHERE Phone=$phone", ("$phone", phone))?.ToString();
    if (string.IsNullOrWhiteSpace(username)) return Results.BadRequest("手机号没有绑定用户");

    var identity = new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.Name, username) },
        CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    smsCodes.TryRemove(phone, out _);
    return Results.Ok(new { name = username });
});

app.MapPost("/api/auth/reset-password", (ResetPasswordDto dto) =>
{
    if (!ValidPhone(dto.Phone)) return Results.BadRequest("请输入8-15位有效手机号，可带+号");
    if (string.IsNullOrWhiteSpace(dto.Code)) return Results.BadRequest("请输入验证码");
    if (!StrongPassword(dto.NewPassword))
        return Results.BadRequest("新密码至少8位，并且必须同时包含字母和数字");

    var phone = dto.Phone!.Trim();
    if (!smsCodes.TryGetValue(phone, out var info))
        return Results.BadRequest("请先获取验证码");
    if (DateTime.UtcNow > info.ExpireTime)
    {
        smsCodes.TryRemove(phone, out _);
        return Results.BadRequest("验证码已过期");
    }
    if (info.Code != dto.Code.Trim())
        return Results.BadRequest("验证码错误");

    var changed = Exec("UPDATE Users SET PasswordHash=$p WHERE Phone=$phone",
        ("$p", Hash(dto.NewPassword)), ("$phone", phone));
    if (changed == 0) return Results.BadRequest("手机号没有绑定用户");

    smsCodes.TryRemove(phone, out _);
    return Results.Ok(new { message = "密码修改成功" });
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


app.MapGet("/api/appointments", [Authorize] () => Results.Ok(Query(@"
SELECT a.Id as id,a.CustomerId as customerId,COALESCE(c.Name,'') as customerName,
       a.PetId as petId,COALESCE(p.Name,'') as petName,a.ServiceType as serviceType,
       a.AppointmentTime as appointmentTime,a.Staff as staff,a.Status as status,
       a.Remark as remark,a.CreatedAt as createdAt
FROM Appointments a
LEFT JOIN Customers c ON c.Id=a.CustomerId
LEFT JOIN Pets p ON p.Id=a.PetId
ORDER BY a.AppointmentTime DESC")));

app.MapPost("/api/appointments", [Authorize] (AppointmentDto d) =>
{
    if (string.IsNullOrWhiteSpace(d.PetId) || Scalar("SELECT 1 FROM Pets WHERE Id=$id", ("$id", d.PetId.Trim())) is null)
        return Results.BadRequest("请选择有效宠物");
    if (string.IsNullOrWhiteSpace(d.AppointmentTime) || !DateTime.TryParse(d.AppointmentTime, out _))
        return Results.BadRequest("请选择预约时间");
    var id = $"APT-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..4].ToUpper()}";
    Exec(@"INSERT INTO Appointments(Id,CustomerId,PetId,ServiceType,AppointmentTime,Staff,Status,Remark,CreatedAt)
           VALUES($id,$customer,$pet,$service,$time,$staff,$status,$remark,$created)",
        ("$id", id), ("$customer", d.CustomerId), ("$pet", d.PetId.Trim()),
        ("$service", d.ServiceType), ("$time", d.AppointmentTime), ("$staff", d.Staff),
        ("$status", string.IsNullOrWhiteSpace(d.Status) ? "待确认" : d.Status),
        ("$remark", d.Remark), ("$created", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
    return Results.Ok(new { id });
});

app.MapPut("/api/appointments/{id}", [Authorize] (string id, AppointmentDto d) =>
    Exec(@"UPDATE Appointments SET CustomerId=$customer,PetId=$pet,ServiceType=$service,
           AppointmentTime=$time,Staff=$staff,Status=$status,Remark=$remark WHERE Id=$id",
        ("$id", id), ("$customer", d.CustomerId), ("$pet", d.PetId), ("$service", d.ServiceType),
        ("$time", d.AppointmentTime), ("$staff", d.Staff), ("$status", d.Status), ("$remark", d.Remark)) > 0
        ? Results.Ok() : Results.NotFound());

app.MapDelete("/api/appointments/{id}", [Authorize] (string id) =>
    Exec("DELETE FROM Appointments WHERE Id=$id", ("$id", id)) > 0 ? Results.Ok() : Results.NotFound());

app.MapGet("/api/members", [Authorize] () => Results.Ok(Query(@"
SELECT m.Id as id,m.CustomerId as customerId,COALESCE(c.Name,'') as customerName,
       COALESCE(c.Phone,'') as phone,m.Level as level,m.Points as points,
       m.Balance as balance,m.Status as status,m.JoinDate as joinDate
FROM Members m
LEFT JOIN Customers c ON c.Id=m.CustomerId
ORDER BY m.JoinDate DESC")));

app.MapPost("/api/members", [Authorize] (MemberDto d) =>
{
    if (string.IsNullOrWhiteSpace(d.CustomerId) || Scalar("SELECT 1 FROM Customers WHERE Id=$id", ("$id", d.CustomerId.Trim())) is null)
        return Results.BadRequest("请选择有效客户");
    if (Scalar("SELECT 1 FROM Members WHERE CustomerId=$id", ("$id", d.CustomerId.Trim())) is not null)
        return Results.BadRequest("该客户已经是会员");
    var id = $"VIP-{DateTime.Now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..5].ToUpper()}";
    Exec(@"INSERT INTO Members(Id,CustomerId,Level,Points,Balance,Status,JoinDate)
           VALUES($id,$customer,$level,$points,$balance,$status,$date)",
        ("$id", id), ("$customer", d.CustomerId.Trim()), ("$level", d.Level ?? "普通会员"),
        ("$points", d.Points), ("$balance", d.Balance), ("$status", d.Status ?? "正常"),
        ("$date", DateTime.Now.ToString("yyyy-MM-dd")));
    return Results.Ok(new { id });
});

app.MapPut("/api/members/{id}", [Authorize] (string id, MemberDto d) =>
    Exec(@"UPDATE Members SET Level=$level,Points=$points,Balance=$balance,Status=$status WHERE Id=$id",
        ("$id", id), ("$level", d.Level), ("$points", d.Points), ("$balance", d.Balance), ("$status", d.Status)) > 0
        ? Results.Ok() : Results.NotFound());

app.MapPost("/api/members/{id}/recharge", [Authorize] (string id, RechargeDto d) =>
{
    if (d.Amount <= 0) return Results.BadRequest("充值金额必须大于0");
    var changed = Exec("UPDATE Members SET Balance=Balance+$amount,Points=Points+CAST($amount AS INTEGER) WHERE Id=$id",
        ("$amount", d.Amount), ("$id", id));
    return changed > 0 ? Results.Ok() : Results.NotFound();
});

app.MapDelete("/api/members/{id}", [Authorize] (string id) =>
    Exec("DELETE FROM Members WHERE Id=$id", ("$id", id)) > 0 ? Results.Ok() : Results.NotFound());

app.MapPost("/api/orders/auto", [Authorize] (OrderCreateDto d) =>
{
    var id = NewOrderId("ORD");
    Exec(@"INSERT INTO Orders(Id,PetId,ServiceType,Price,CreateTime,Status,PaymentStatus)
           VALUES($id,$pet,$type,$price,$time,$status,$pay)",
        ("$id", id), ("$pet", d.PetId), ("$type", d.ServiceType), ("$price", d.Price),
        ("$time", DateTime.Now.ToString("yyyy-MM-dd HH:mm")), ("$status", d.Status ?? "待完成"),
        ("$pay", "待支付"));
    return Results.Ok(new { id });
});

app.MapPost("/api/orders/{id}/checkout", [Authorize] (string id, CheckoutDto d) =>
{
    var priceObj = Scalar("SELECT Price FROM Orders WHERE Id=$id", ("$id", id));
    if (priceObj is null) return Results.NotFound();
    var price = Convert.ToDouble(priceObj);
    if (string.IsNullOrWhiteSpace(d.PaymentMethod)) return Results.BadRequest("请选择支付方式");

    if (d.PaymentMethod == "会员余额")
    {
        if (string.IsNullOrWhiteSpace(d.MemberId)) return Results.BadRequest("请选择会员");
        var balanceObj = Scalar("SELECT Balance FROM Members WHERE Id=$id AND Status='正常'", ("$id", d.MemberId));
        if (balanceObj is null) return Results.BadRequest("会员不存在或状态异常");
        var balance = Convert.ToDouble(balanceObj);
        if (balance < price) return Results.BadRequest("会员余额不足");
        Exec("UPDATE Members SET Balance=Balance-$price,Points=Points+CAST($price AS INTEGER) WHERE Id=$id",
            ("$price", price), ("$id", d.MemberId));
    }
    else if (!string.IsNullOrWhiteSpace(d.MemberId))
    {
        Exec("UPDATE Members SET Points=Points+CAST($price AS INTEGER) WHERE Id=$id",
            ("$price", price), ("$id", d.MemberId));
    }

    Exec(@"UPDATE Orders SET PaymentStatus='已支付',PaymentMethod=$method,PaidAt=$paid,Status='已完成' WHERE Id=$id",
        ("$method", d.PaymentMethod), ("$paid", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")), ("$id", id));
    return Results.Ok(new { id, amount = price });
});

app.MapGet("/api/checkout/orders", [Authorize] () => Results.Ok(Query(@"
SELECT o.Id as id,o.PetId as petId,COALESCE(p.Name,'') as petName,o.ServiceType as serviceType,
       o.Price as price,o.CreateTime as createTime,o.Status as status,
       COALESCE(o.PaymentStatus,'待支付') as paymentStatus,
       COALESCE(o.PaymentMethod,'') as paymentMethod,COALESCE(o.PaidAt,'') as paidAt
FROM Orders o LEFT JOIN Pets p ON p.Id=o.PetId
ORDER BY o.CreateTime DESC")));

app.MapGet("/api/logs", [Authorize] () => Results.Ok(Query(@"
SELECT Id as id,Actor as actor,Method as method,Path as path,StatusCode as statusCode,CreatedAt as createdAt
FROM OperationLogs ORDER BY Id DESC LIMIT 500")));

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

record LoginDto(string? Name, string? Password, string? Phone);
record SmsSendDto(string? Phone);
record SmsLoginDto(string? Phone, string? Code);
record ResetPasswordDto(string? Phone, string? Code, string? NewPassword);
record SmsCodeInfo(string Code, DateTime ExpireTime);
record AppointmentDto(string? CustomerId, string? PetId, string ServiceType, string AppointmentTime, string? Staff, string? Status, string? Remark);
record MemberDto(string? CustomerId, string? Level, int Points, double Balance, string? Status);
record RechargeDto(double Amount);
record OrderCreateDto(string? PetId, string? ServiceType, double Price, string? Status);
record CheckoutDto(string? PaymentMethod, string? MemberId);
record PetDto(string Id, string Name, int? Age, string? Breed, string? Gender, double? Weight);
record CustomerDto(string Id, string Name, string? Phone, string? Address, string? PetId);
record WashDto(string Id, string? PetId, string Name, double Price, string? Time);
record BoardingDto(string Id, string? PetId, string StartDate, string EndDate, double PricePerDay, string? Status);
record OrderDto(string Id, string? PetId, string? ServiceType, double Price, string? CreateTime, string? Status);
