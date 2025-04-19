using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Org.BouncyCastle.Pqc.Crypto.Frodo;




namespace Geni_Project {

    class SmtpConfig {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public bool EnableSsl { get; set; }
        public string User { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    class MailConfig {
        public string From { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
    }

    class AppConfig {
        public SmtpConfig Smtp { get; set; } = new();
        public MailConfig Mail { get; set; } = new();
        public List<string> Recipients { get; set; } = [];
    }
    
    
    public record Launch(string Id, string Name, DateTime Net, DateTime LastUpdated, string ImgUrl, string ProviderName, string LocationName, string Status = "Upcoming");

    class LaunchDto {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("net")] public DateTime Net { get; set; }
        [JsonPropertyName("last_updated")] public DateTime LastUpdated { get; set; }
        [JsonPropertyName("image")] public Image? Image { get; set; }
        [JsonPropertyName("launch_service_provider")] public Provider? Provider { get; set; }
        [JsonPropertyName("pad")] public Pad? Pad { get; set; }

        public Launch ToLaunch() {
            return new Launch(Id, Name, Net, LastUpdated, Image?.ImageUrl ?? "", Provider?.Name ?? "", Pad?.Location?.Name ?? "", "Upcoming");
        }
    }

    class Image { [JsonPropertyName("image_url")] public string ImageUrl { get; set; } = ""; }
    class Provider { [JsonPropertyName("name")] public string Name { get; set; } = ""; }
    class Pad { [JsonPropertyName("location")] public Location? Location { get; set; } }
    class Location {[JsonPropertyName("name")] public string Name { get; set; } = ""; }

    class LaunchResponse { [JsonPropertyName("results")] public List<LaunchDto> Results { get; set; } = []; }




    public static class Program {
        const string DbPath = "launches.db";
        const string Api = "https://ll.thespacedevs.com/2.3.0/launches/";
        const string Schema = """
            CREATE TABLE IF NOT EXISTS Launches (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            Net TEXT NOT NULL,
            LastUpdated TEXT NOT NULL,
            ImgUrl TEXT,
            ProviderName TEXT,
            LocationName TEXT,
            Status TEXT NOT NULL DEFAULT 'Upcoming',
            UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_launches_updatedat ON Launches(UpdatedAt);
            """;

        static async Task Main(string[] args) {
                
            var fresh = await GetUpcomingLaunchesAsync();
            await SyncAsync(fresh);
            Console.WriteLine("Sync complete.");

            
            var cfg = LoadConfig("config.json");

            
            var updates = GetTodaysUpdates();
            if (updates.Count == 0) {
                Console.WriteLine("No new or changed launches today. No mail sent.");
                return;
            }

            Console.WriteLine($"Sending mail for {updates.Count} updates...");
            SendMail(updates, cfg);
            Console.WriteLine("Mail sent.");
        }

        static AppConfig LoadConfig(string path) {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? throw new InvalidOperationException("Invalid config.json");
        }
        
        public static SqliteConnection OpenAndInit() {
            var conn = new SqliteConnection($"Data Source={DbPath}");  
            conn.Open();

            
            new SqliteCommand(Schema, conn).ExecuteNonQuery();
            return conn;
        }

        public static async Task<List<Launch>> GetUpcomingLaunchesAsync() {

            var today = DateTime.UtcNow.Date;
            var week   = today.AddDays(7);
            var url = $"{Api}?net__gte={today:o}&net__lte={week:o}&limit=100&ordering=net";

            using var http = new HttpClient();
            var root = await http.GetFromJsonAsync<LaunchResponse>(url);
            return root?.Results.Select(dto => dto.ToLaunch()).ToList() ?? [];
        }

        public static async Task SyncAsync(List<Launch> fresh) {
            await using var db = OpenAndInit();
            using var tx = db.BeginTransaction();                   

            var now = DateTime.UtcNow.ToString("o");
            var delete = new SqliteCommand("""
                DELETE 
                FROM Launches 
                WHERE Net < @now OR Status = 'Cancelled';
                """,
                db, tx);
            delete.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            await delete.ExecuteNonQueryAsync();

            var existing = new Dictionary<string,string>();

            var findNetId = new SqliteCommand("""
                SELECT Id, Net 
                FROM Launches 
                """, db, tx);
            var reader = await findNetId.ExecuteReaderAsync();
            while (await reader.ReadAsync()) {
                existing[reader.GetString(0)] = reader.GetString(1);
            }

            var freshIds    = fresh.Select(l => l.Id).ToHashSet();
            var cancelled   = existing.Keys.Except(freshIds);
            foreach(var id in cancelled) {
                var upd = new SqliteCommand("""
                    UPDATE Launches
                    SET UpdatedAt = @now,
                        Status = 'Cancelled'
                    WHERE Id = @id;
                    """, db, tx);
                upd.Parameters.AddWithValue("@id",  id);
                upd.Parameters.AddWithValue("@now", now);
                await upd.ExecuteNonQueryAsync();
            }



            const string upsertSql = """
                INSERT INTO Launches (Id, Name, Net, LastUpdated, ImgUrl, ProviderName, LocationName, UpdatedAt)
                VALUES (@id, @name, @net, @lastUpdated, @imgUrl, @providerName, @locationName, @updatedAt)
                ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                Net = excluded.Net,
                LastUpdated = excluded.LastUpdated,
                ImgUrl = excluded.ImgUrl,
                ProviderName = excluded.ProviderName,
                LocationName = excluded.LocationName,
                Status = 'Upcoming',
                UpdatedAt = CASE
                    WHEN excluded.Net != Launches.Net
                    THEN excluded.UpdatedAt
                    ELSE Launches.UpdatedAt
                END;
            """;

            var upsert = new SqliteCommand(upsertSql, db, tx);
            upsert.Parameters.Add("@id", SqliteType.Text);
            upsert.Parameters.Add("@name", SqliteType.Text);
            upsert.Parameters.Add("@net", SqliteType.Text);
            upsert.Parameters.Add("@lastUpdated", SqliteType.Text);
            upsert.Parameters.Add("@imgUrl", SqliteType.Text);
            upsert.Parameters.Add("@providerName", SqliteType.Text);
            upsert.Parameters.Add("@locationName", SqliteType.Text);
            upsert.Parameters.Add("@updatedAt", SqliteType.Text);


            
            foreach (var l in fresh) {
                var newNet = l.Net.ToString("o");
                if (existing.TryGetValue(l.Id, out var oldNet) && oldNet == newNet)
                    continue;

                upsert.Parameters["@id"].Value = l.Id;
                upsert.Parameters["@name"].Value = l.Name;
                upsert.Parameters["@net"].Value = newNet;
                upsert.Parameters["@lastUpdated"].Value = l.LastUpdated.ToString("o");
                upsert.Parameters["@imgUrl"].Value = l.ImgUrl;
                upsert.Parameters["@providerName"].Value = l.ProviderName;
                upsert.Parameters["@locationName"].Value = l.LocationName;
                upsert.Parameters["@updatedAt"].Value = now;

                await upsert.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
        static List<Launch> GetTodaysUpdates() {
            using var db = OpenAndInit();
            var cmd = new SqliteCommand(
                """
                SELECT Id,Name,Net,LastUpdated,ImgUrl,ProviderName,LocationName, Status
                FROM Launches 
                WHERE date(UpdatedAt)=date('now');
                """, db);
            var list = new List<Launch>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) {
                list.Add(new Launch(
                    r.GetString(0),
                    r.GetString(1),
                    DateTime.Parse(r.GetString(2)),
                    DateTime.Parse(r.GetString(3)),
                    r.IsDBNull(4)? "" : r.GetString(4),
                    r.IsDBNull(5)? "" : r.GetString(5),
                    r.IsDBNull(6)? "" : r.GetString(6),
                    r.GetString(7)
                ));
            }
            return list;
        }
        static void SendMail(List<Launch> updates, AppConfig cfg) {
            var sb = new StringBuilder();
            sb.AppendLine("<h2>Rocket Launch Updates</h2>");
            sb.AppendLine("<table border=\"1\" cellpadding=\"4\" cellspacing=\"0\">");
            sb.AppendLine("<tr><th>Name</th><th>NET</th><th>Provider</th><th>Location</th></tr>");
            foreach (var l in updates) {

                var name = WebUtility.HtmlEncode(l.Name);
                var net = $"{l.Net:yyyy-MM-dd HH:mm} UTC";
                var provider = WebUtility.HtmlEncode(l.ProviderName);
                var location = WebUtility.HtmlEncode(l.LocationName);

                if (l.Status == "Cancelled") {
                    name = $"<s>{name}</s>";
                    net = $"<s>{net}</s>";
                    provider = $"<s>{provider}</s>";
                    location = $"<s>{location}</s>";
                }

                sb.AppendLine("<tr>");
                sb.AppendLine($"  <td>{name}</td>");
                sb.AppendLine($"  <td>{net}</td>");
                sb.AppendLine($"  <td>{provider}</td>");
                sb.AppendLine($"  <td>{location}</td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</table>");
            var body = sb.ToString();

            using var mail = new MailMessage {
                From = new MailAddress(cfg.Mail.From),
                Subject = cfg.Mail.Subject.Replace("{{DATE}}", DateTime.UtcNow.ToString("yyyy-MM-dd")),
                Body = body,
                IsBodyHtml = true
            };
            foreach (var rcpt in cfg.Recipients) {
                mail.To.Add(rcpt);
            }

            using var smtp = new SmtpClient(cfg.Smtp.Host, cfg.Smtp.Port) {
                EnableSsl = cfg.Smtp.EnableSsl,
                Credentials = new NetworkCredential(cfg.Smtp.User, cfg.Smtp.Password)
            };
            smtp.Send(mail);
        }
    }
}