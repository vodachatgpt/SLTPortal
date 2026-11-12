using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.Entity;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using SLTProject.Data;

namespace SLTProject.Services
{
    public sealed class GuestDistributionInput
    {
        public bool All { get; set; }
        public List<long> GuestIds { get; set; }
        public bool SendEmail { get; set; }
        public bool SendSms { get; set; }
        public bool ForceResend { get; set; }
    }

    public sealed class GuestImportModeResult
    {
        public string Mode { get; set; }
        public int TotalRows { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public int SoftDeleted { get; set; }
        public int Failed
        {
            get
            {
                return ErrorDetails == null
                    ? 0
                    : ErrorDetails.Select(x => x.RowNumber).Distinct().Count();
            }
        }
        public bool Success { get { return Failed == 0; } }
        public List<string> Errors { get; set; } = new List<string>();
        public List<ImportErrorDetail> ErrorDetails { get; set; } =
            new List<ImportErrorDetail>();
        public List<ImportRowResult> Rows { get; set; } =
            new List<ImportRowResult>();

        public void AddError(int row, string column, string value, string reason)
        {
            column = string.IsNullOrWhiteSpace(column) ? "Record" : column;
            reason = string.IsNullOrWhiteSpace(reason) ? "Invalid row." : reason;

            ErrorDetails.Add(new ImportErrorDetail
            {
                RowNumber = row,
                Column = column,
                Value = value,
                Reason = reason
            });

            Errors.Add("Row " + row + ", " + column + ": " + reason);
            Skipped++;
        }
    }

    public sealed class GuestDistributionResult
    {
        public int Selected { get; set; }
        public int EmailAccepted { get; set; }
        public int SmsAccepted { get; set; }
        public int Skipped { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    public sealed class GuestStatisticsResult
    {
        public int Total { get; set; }
        public int LoggedIn { get; set; }
        public int NotLoggedIn { get; set; }
        public int TourRegistered { get; set; }
        public int TourNotRegistered { get; set; }
        public int EmailSent { get; set; }
        public int SmsSent { get; set; }
    }

    // EF6 DB-first service: update your EDMX after running the SQL migration.
    // SmsTemplate and GuestMessageDelivery are the SQL-generated EF entities.
    public sealed class GuestOperationsService
    {
        private const int MaxImportRows = 10000;
        private static readonly string[] Columns = {
            "Email", "FullName", "PhoneNumber", "ArrivalDate", "ArrivalFlight", "LandingTime",
            "DepartureDate", "DepartureFlight", "DepartureTime", "HotelName", "CheckInDate",
            "CheckInTime", "CheckOutDate", "CheckOutTime", "TourRegister", "WalletUrl",
            "NotificationMode", "NotificationChannels"
        };

        private static string Normalize(string text) { return (text ?? "").Trim(); }
        private static string NormalEmail(string text) { return Normalize(text).ToLowerInvariant(); }
        private static string Key(string name)
        {
            return new string((name ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_').ToArray());
        }
        private static string Value(IXLWorksheet sheet, int r, Dictionary<string, int> headings, string column)
        {
            int col;
            return headings.TryGetValue(column, out col) ? Normalize(sheet.Cell(r, col).GetFormattedString()) : "";
        }
        private static DateTime? ParseDate(string text, string name, int row)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            DateTime value;
            string[] acceptedFormats =
            {
                "dd/MM/yyyy", "d/M/yyyy",
                "yyyy-MM-dd", "yyyy-M-d"
            };

            if (!DateTime.TryParseExact(
                text.Trim(),
                acceptedFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value))
            {
                // Avoid interpreting an ambiguous slash date using the
                // server culture. Slash dates must follow day/month/year.
                if (text.IndexOf('/') >= 0 ||
                    !DateTime.TryParse(
                        text,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out value))
                {
                    throw new ArgumentException(
                        "Row " + row + ": invalid " + name +
                        " date. Use dd/MM/yyyy (for example 04/12/2026).");
                }
            }

            return value.Date;
        }
        private static TimeSpan? ParseTime(string text, string name, int row)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            try
            {
                return ScheduleTransportExcel.Time(text, name);
            }
            catch (ArgumentException)
            {
                throw new ArgumentException(
                    "Row " + row + ": " + name +
                    " must use 24-hour HH:mm format (for example 20:35).");
            }
        }

        private static bool? ParseBool(string value, int row)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            switch (value.ToLowerInvariant())
            {
                case "yes": case "y": case "true": case "1": return true;
                case "no": case "n": case "false": case "0": return false;
                default: throw new ArgumentException("Row " + row + ": TourRegister must be Yes/No.");
            }
        }
        private static void EnsurePhone(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return;
            if (phone.Length > 30 || !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^\+[1-9]\d{0,3}(?:[ \-()]?\d){4,17}$"))
                throw new ArgumentException("Phone number must include international prefix, e.g. +201001234567.");
        }
        private static void EnsureUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            Uri uri;
            if (url.Length > 1000 || !Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("WalletUrl must be a valid HTTPS URL (max 1000 characters).");
        }
        private static void Apply(Guest g, GuestInput input, string walletUrl)
        {
            GuestsService.Merge(g, input); // Blank cells never overwrite existing values.
            if (!string.IsNullOrWhiteSpace(walletUrl)) { EnsureUrl(walletUrl); g.WalletUrl = walletUrl; }
        }
        private sealed class ImportRow
        {
            public int RowNumber;
            public GuestInput Input;
            public string Wallet;
        }

        private sealed class ParsedGuestImport
        {
            public List<ImportRow> Rows { get; set; } = new List<ImportRow>();
            public List<ImportErrorDetail> Errors { get; set; } = new List<ImportErrorDetail>();
            public int TotalRows { get; set; }
            public int BlankRows { get; set; }
        }

        private static ParsedGuestImport ReadRows(byte[] file)
        {
            if (file == null || file.Length == 0 || file.Length > 10 * 1024 * 1024)
                throw new ArgumentException("Excel file must be non-empty and at most 10 MB.");

            var result = new ParsedGuestImport();

            using (var ms = new MemoryStream(file))
            using (var wb = new XLWorkbook(ms))
            {
                var ws = wb.Worksheets.FirstOrDefault();
                if (ws == null) throw new ArgumentException("Workbook has no worksheet.");

                int lastCol = ws.LastColumnUsed() == null ? 0 : ws.LastColumnUsed().ColumnNumber();
                var headings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                for (int c = 1; c <= lastCol; c++)
                {
                    string heading = Key(ws.Cell(1, c).GetString());
                    if (heading.Length == 0) continue;

                    string canonical = Columns.FirstOrDefault(x =>
                        string.Equals(Key(x), heading, StringComparison.OrdinalIgnoreCase));

                    if (canonical == null)
                        throw new ArgumentException("Unknown Excel column: " + ws.Cell(1, c).GetString().Trim());

                    if (headings.ContainsKey(canonical))
                        throw new ArgumentException("Duplicate column: " + canonical);

                    headings.Add(canonical, c);
                }

                if (!headings.ContainsKey("Email"))
                    throw new ArgumentException("Excel must contain an Email column.");

                int lastRow = ws.LastRowUsed() == null ? 1 : ws.LastRowUsed().RowNumber();
                if (lastRow > MaxImportRows + 1)
                    throw new ArgumentException("Maximum import size is 10,000 guests.");

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int r = 2; r <= lastRow; r++)
                {
                    result.TotalRows++;

                    var data = Columns.ToDictionary(x => x, x => Value(ws, r, headings, x));

                    if (data.Values.All(string.IsNullOrWhiteSpace))
                    {
                        result.BlankRows++;
                        continue;
                    }

                    try
                    {
                        string email = NormalEmail(data["Email"]);

                        if (string.IsNullOrWhiteSpace(email) ||
                            !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
                            throw new ArgumentException("Email is missing or has an invalid format.");

                        if (!seen.Add(email))
                            throw new ArgumentException("Duplicate Email in this workbook.");

                        EnsurePhone(data["PhoneNumber"]);
                        EnsureUrl(data["WalletUrl"]);

                        var input = new GuestInput
                        {
                            Email = email,
                            FullName = data["FullName"],
                            PhoneNumber = data["PhoneNumber"],
                            ArrivalDate = ParseDate(data["ArrivalDate"], "ArrivalDate", r),
                            ArrivalFlight = data["ArrivalFlight"],
                            LandingTime = ParseTime(data["LandingTime"], "LandingTime", r),
                            DepartureDate = ParseDate(data["DepartureDate"], "DepartureDate", r),
                            DepartureFlight = data["DepartureFlight"],
                            DepartureTime = ParseTime(data["DepartureTime"], "DepartureTime", r),
                            HotelName = data["HotelName"],
                            CheckInDate = ParseDate(data["CheckInDate"], "CheckInDate", r),
                            CheckInTime = ParseTime(data["CheckInTime"], "CheckInTime", r),
                            CheckOutDate = ParseDate(data["CheckOutDate"], "CheckOutDate", r),
                            CheckOutTime = ParseTime(data["CheckOutTime"], "CheckOutTime", r),
                            TourRegister = ParseBool(data["TourRegister"], r)
                        };

                        result.Rows.Add(new ImportRow
                        {
                            RowNumber = r,
                            Wallet = data["WalletUrl"],
                            Input = input
                        });
                    }
                    catch (Exception ex)
                    {
                        string column = ScheduleTransportExcel.ErrorColumn(
                            ex.Message, Columns);

                        if (column == "Record")
                        {
                            string message = (ex.Message ?? "").ToLowerInvariant();
                            if (message.Contains("phone number")) column = "PhoneNumber";
                            else if (message.Contains("walleturl")) column = "WalletUrl";
                            else if (message.Contains("email")) column = "Email";
                        }

                        string value = data.ContainsKey(column) ? data[column] : "";
                        string reason = ex.Message ?? "Invalid value.";
                        string rowPrefix = "Row " + r + ": ";
                        if (reason.StartsWith(rowPrefix, StringComparison.OrdinalIgnoreCase))
                            reason = reason.Substring(rowPrefix.Length);

                        result.Errors.Add(new ImportErrorDetail
                        {
                            RowNumber = r,
                            Column = column,
                            Value = value,
                            Reason = reason
                        });
                    }
                }
            }

            if (result.TotalRows == 0 && result.BlankRows == 0)
                throw new ArgumentException("Workbook has no guest rows.");

            return result;
        }

        public byte[] BuildTemplate()
        {
            using (var wb = new XLWorkbook())
            {
                var ws = wb.Worksheets.Add("Guests");
                for (int i = 0; i < Columns.Length; i++)
                {
                    ws.Cell(1, i + 1).Value = Columns[i];

                    string columnName = Columns[i];
                    if (columnName.EndsWith("Date", StringComparison.OrdinalIgnoreCase))
                        ws.Column(i + 1).Style.DateFormat.Format = "dd/MM/yyyy";
                    else if (columnName.EndsWith("Time", StringComparison.OrdinalIgnoreCase))
                        ws.Column(i + 1).Style.DateFormat.Format = "HH:mm";
                }

                ws.Range(1, 1, 1, Columns.Length).Style.Font.Bold = true;
                ws.Range(1, 1, 1, Columns.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#C8102E");
                ws.Range(1, 1, 1, Columns.Length).Style.Font.FontColor = XLColor.White;
                ws.SheetView.FreezeRows(1);
                ws.Columns().AdjustToContents();
                ws.Cell(3, 1).Value = "Email is required for every row. For existing guests, supply ONLY the columns you need to change. Blank cells retain existing values. For NEW guests include FullName. Do not upload this guidance row.";
                ws.Cell(3, 1).Style.Font.FontColor = XLColor.Gray;
                // Put guidance in separate sheet so the actual import tab has header only.
                ws.Cell(3, 1).Clear();
                var guide = wb.Worksheets.Add("Instructions");
                guide.Cell(1, 1).Value = "Email is mandatory. FullName is mandatory when adding new guests.";
                guide.Cell(2, 1).Value = "You can remove unused columns from Guests worksheet. Blank cells never clear existing values.";
                guide.Cell(3, 1).Value = "Dates: dd/MM/yyyy; Times: HH:mm (24-hour); TourRegister: Yes/No; PhoneNumber: +201001234567";
                guide.Cell(4, 1).Value = "Import modes: ReplaceAll, UpdateOnly, Upsert. ReplaceAll soft-deletes existing guests.";
                guide.Cell(6, 1).Value = "CheckInDate/Time or CheckOutDate/Time must be supplied to notify that field. Empty cells preserve existing values.";
                guide.Columns().AdjustToContents();
                using (var result = new MemoryStream()) { wb.SaveAs(result); return result.ToArray(); }
            }
        }

        public async Task<GuestImportModeResult> ImportAsync(
            byte[] bytes,
            string mode,
            string actor)
        {
            string[] allowedModes = { "ReplaceAll", "UpdateOnly", "Upsert" };

            if (!allowedModes.Contains(mode, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "Import mode must be ReplaceAll, UpdateOnly or Upsert.");

            mode = allowedModes.First(x =>
                x.Equals(mode, StringComparison.OrdinalIgnoreCase));

            var parsed = ReadRows(bytes);
            var result = new GuestImportModeResult
            {
                Mode = mode,
                TotalRows = parsed.TotalRows,
                Skipped = parsed.BlankRows
            };

            foreach (var error in parsed.Errors)
                result.AddError(
                    error.RowNumber,
                    error.Column,
                    error.Value,
                    error.Reason);

            using (var db = new SLTEntities())
            {
                var existing = await db.Guests
                    .Where(g => !g.IsDeleted)
                    .ToListAsync();

                var map = existing.ToDictionary(
                    g => NormalEmail(g.Email),
                    StringComparer.OrdinalIgnoreCase);

                var validRows = new List<ImportRow>();

                foreach (var row in parsed.Rows)
                {
                    Guest current;
                    bool exists = mode != "ReplaceAll" &&
                        map.TryGetValue(row.Input.Email, out current);

                    if (mode == "UpdateOnly" && !exists)
                    {
                        result.AddError(
                            row.RowNumber,
                            "Email",
                            row.Input.Email,
                            "Guest email was not found. UpdateOnly mode does not create guests.");
                        continue;
                    }

                    bool willCreate = mode == "ReplaceAll" ||
                        (mode == "Upsert" && !exists);

                    if (willCreate && string.IsNullOrWhiteSpace(row.Input.FullName))
                    {
                        result.AddError(
                            row.RowNumber,
                            "FullName",
                            row.Input.FullName,
                            "FullName is required when creating a new guest.");
                        continue;
                    }

                    validRows.Add(row);
                }

                // Do not perform destructive ReplaceAll when every input row
                // failed validation. Valid rows are still processed separately.
                if (mode == "ReplaceAll" && validRows.Count > 0)
                {
                    DateTime replaceTime = EgyptDateTimeService.Now;

                    foreach (var guest in existing)
                    {
                        guest.IsDeleted = true;
                        guest.DeletedBy = actor;
                        guest.DeletedAt = replaceTime;
                        guest.SessionTokenHash = null;
                        guest.SessionExpiresAt = null;
                        guest.FirstLoginAt = null;
                        guest.ModifiedBy = actor;
                        guest.RetentionDateModified = replaceTime;
                    }

                    await db.SaveChangesAsync();
                    result.SoftDeleted = existing.Count;
                    map.Clear();
                }

                foreach (var row in validRows)
                {
                    Guest current = null;
                    bool exists = mode != "ReplaceAll" &&
                        map.TryGetValue(row.Input.Email, out current);

                    try
                    {
                        if (exists)
                        {
                            Apply(current, row.Input, row.Wallet);
                            current.ModifiedBy = actor;
                            current.RetentionDateModified = EgyptDateTimeService.Now;

                            await db.SaveChangesAsync();

                            result.Updated++;
                            result.Rows.Add(new ImportRowResult
                            {
                                Row = row.RowNumber,
                                Email = row.Input.Email,
                                Action = "Updated",
                                Passcode = null
                            });
                        }
                        else
                        {
                            if (mode == "UpdateOnly")
                            {
                                result.AddError(
                                    row.RowNumber,
                                    "Email",
                                    row.Input.Email,
                                    "Guest email was not found. UpdateOnly mode does not create guests.");
                                continue;
                            }

                            var created = GuestsService.CreateInside(
                                db, row.Input, actor);

                            var newGuest = db.Guests.Local.FirstOrDefault(g =>
                                NormalEmail(g.Email) == row.Input.Email &&
                                !g.IsDeleted);

                            if (newGuest == null)
                                throw new InvalidOperationException(
                                    "The guest record could not be created.");

                            if (!string.IsNullOrWhiteSpace(row.Wallet))
                                newGuest.WalletUrl = row.Wallet;

                            // Notifications are intentionally not sent by import.
                            newGuest.EmailSent = false;
                            newGuest.SmsSent = false;

                            await db.SaveChangesAsync();

                            map[row.Input.Email] = newGuest;
                            result.Created++;
                            result.Rows.Add(new ImportRowResult
                            {
                                Row = row.RowNumber,
                                Email = row.Input.Email,
                                Action = "Created",
                                Passcode = created.Passcode
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        var pending = db.ChangeTracker.Entries()
                            .Where(x =>
                                x.State == System.Data.Entity.EntityState.Added ||
                                x.State == System.Data.Entity.EntityState.Modified ||
                                x.State == System.Data.Entity.EntityState.Deleted)
                            .ToList();

                        foreach (var entry in pending)
                            entry.State = System.Data.Entity.EntityState.Detached;

                        string column = ScheduleTransportExcel.ErrorColumn(
                            ex.Message, Columns);

                        if (column == "Record")
                        {
                            string message = (ex.Message ?? "").ToLowerInvariant();
                            if (message.Contains("email")) column = "Email";
                            else if (message.Contains("fullname")) column = "FullName";
                            else if (message.Contains("phone number")) column = "PhoneNumber";
                            else if (message.Contains("walleturl")) column = "WalletUrl";
                        }

                        string value = "";
                        switch (column)
                        {
                            case "Email": value = row.Input.Email; break;
                            case "FullName": value = row.Input.FullName; break;
                            case "PhoneNumber": value = row.Input.PhoneNumber; break;
                            case "WalletUrl": value = row.Wallet; break;
                        }

                        result.AddError(
                            row.RowNumber,
                            column,
                            value,
                            "The row could not be saved: " + ex.Message);
                    }
                }
            }

            return result;
        }

        public async Task<object> RowDetailsAsync(List<long> ids)
        {
            if (ids == null || ids.Count == 0 || ids.Count > 200)
                throw new ArgumentException("Provide 1 to 200 guest IDs.");
            using (var db = new SLTEntities())
            {
                return await db.Guests.AsNoTracking()
                    .Where(g => ids.Contains(g.Id) && !g.IsDeleted)
                    .Select(g => new {
                        g.Id, g.EmailSent, g.SmsSent, g.WalletUrl,
                        g.CreatedBy, g.RetentionDateCreated,
                        g.ModifiedBy, g.RetentionDateModified
                    }).ToListAsync();
            }
        }

        public async Task<GuestStatisticsResult> StatisticsAsync()
        {
            using (var db = new SLTEntities())
            {
                var q = db.Guests.AsNoTracking().Where(g => !g.IsDeleted);
                var stats = new GuestStatisticsResult();
                stats.Total = await q.CountAsync();
                stats.LoggedIn = await q.CountAsync(g => g.FirstLoginAt != null);
                stats.TourRegistered = await q.CountAsync(g => g.TourRegister);
                stats.EmailSent = await q.CountAsync(g => g.EmailSent);
                stats.SmsSent = await q.CountAsync(g => g.SmsSent);
                stats.NotLoggedIn = stats.Total - stats.LoggedIn;
                stats.TourNotRegistered = stats.Total - stats.TourRegistered;
                return stats;
            }
        }

        public async Task<byte[]> ExportAsync(string segment)
        {
            string[] allowed = { "all", "logged-in", "not-logged-in", "tour-registered", "tour-not-registered" };
            if (!allowed.Contains(segment)) throw new ArgumentException("Invalid export segment.");
            using (var db = new SLTEntities())
            {
                var q = db.Guests.AsNoTracking().Where(g => !g.IsDeleted);
                if (segment == "logged-in") q = q.Where(g => g.FirstLoginAt != null);
                if (segment == "not-logged-in") q = q.Where(g => g.FirstLoginAt == null);
                if (segment == "tour-registered") q = q.Where(g => g.TourRegister);
                if (segment == "tour-not-registered") q = q.Where(g => !g.TourRegister);
                using (var wb = new XLWorkbook())
                {
                    var ws = wb.Worksheets.Add("Guests");
                    string[] headings = { "Id", "FullName", "Email", "PhoneNumber", "ArrivalDate", "LandingTime", "DepartureDate", "DepartureTime", "HotelName", "CheckInDate", "CheckInTime", "CheckOutDate", "CheckOutTime", "TourRegister", "LoggedIn", "EmailSent", "SmsSent", "WalletUrl", "CreatedBy", "RetentionDateCreated", "ModifiedBy", "RetentionDateModified" };
                    for (int i = 0; i < headings.Length; i++) ws.Cell(1, i + 1).Value = headings[i];
                    int row = 2;
                    const int batch = 500;
                    long after = 0;
                    while (true)
                    {
                        var guests = await q.Where(g => g.Id > after).OrderBy(g => g.Id).Take(batch).ToListAsync();
                        if (guests.Count == 0) break;
                        foreach (var g in guests)
                        {
                            object[] values = { g.Id, g.FullName, g.Email, g.PhoneNumber,
                                g.ArrivalDate.HasValue ? EgyptDateTimeService.FormatDate(g.ArrivalDate.Value) : "",
                                g.LandingTime.HasValue ? EgyptDateTimeService.FormatTime(g.LandingTime.Value) : "",
                                g.DepartureDate.HasValue ? EgyptDateTimeService.FormatDate(g.DepartureDate.Value) : "",
                                g.DepartureTime.HasValue ? EgyptDateTimeService.FormatTime(g.DepartureTime.Value) : "",
                                g.HotelName, g.CheckInDate.HasValue ? EgyptDateTimeService.FormatDate(g.CheckInDate.Value) : "",
                                g.CheckInTime.HasValue ? EgyptDateTimeService.FormatTime(g.CheckInTime.Value) : "",
                                g.CheckOutDate.HasValue ? EgyptDateTimeService.FormatDate(g.CheckOutDate.Value) : "",
                                g.CheckOutTime.HasValue ? EgyptDateTimeService.FormatTime(g.CheckOutTime.Value) : "",
                                g.TourRegister ? "Yes" : "No", g.FirstLoginAt.HasValue ? "Yes" : "No",
                                g.EmailSent ? "Yes" : "No", g.SmsSent ? "Yes" : "No", g.WalletUrl,
                                g.CreatedBy, EgyptDateTimeService.FormatDateTime(g.RetentionDateCreated),
                                g.ModifiedBy, g.RetentionDateModified.HasValue ? EgyptDateTimeService.FormatDateTime(g.RetentionDateModified.Value) : "" };
                            for (int i = 0; i < values.Length; i++)
                            {
                                // Prefix risky values to prevent Excel formula injection.
                                string text = Convert.ToString(values[i], CultureInfo.InvariantCulture) ?? "";
                                if (text.Length > 0 && "=+-@".IndexOf(text[0]) >= 0) text = "'" + text;
                                ws.Cell(row, i + 1).Value = text;
                            }
                            row++;
                        }
                        after = guests.Last().Id;
                    }
                    ws.Range(1, 1, 1, headings.Length).Style.Font.Bold = true;
                    ws.SheetView.FreezeRows(1);
                    ws.Columns().AdjustToContents();
                    using (var output = new MemoryStream()) { wb.SaveAs(output); return output.ToArray(); }
                }
            }
        }

        public async Task<GuestDistributionResult> DistributeAsync(GuestDistributionInput input, string actor)
        {
            if (input == null || (!input.SendEmail && !input.SendSms))
                throw new ArgumentException("Select email and/or SMS.");
            if (!input.All && (input.GuestIds == null || input.GuestIds.Count == 0))
                throw new ArgumentException("Select at least one guest.");
            if (!input.All && input.GuestIds.Distinct().Count() > 500)
                throw new ArgumentException("Maximum 500 recipients per request.");
            List<long> guestIds;
            string smsBody = null;
            using (var db = new SLTEntities())
            {
                var q = db.Guests.Where(g => !g.IsDeleted);
                if (!input.All) q = q.Where(g => input.GuestIds.Contains(g.Id));
                guestIds = await q.OrderBy(g => g.Id).Select(g => g.Id).Take(501).ToListAsync();
                if (guestIds.Count > 500) throw new ArgumentException("More than 500 guests selected; send in batches.");
                if (input.SendSms)
                {
                    var template = await db.Set<SmsTemplate>().AsNoTracking()
                        .FirstOrDefaultAsync(x => x.TemplateKey == "GUEST_INVITATION_SMS" && x.IsActive);
                    if (template == null || string.IsNullOrWhiteSpace(template.TemplateBody))
                        throw new InvalidOperationException("Active GUEST_INVITATION_SMS template is missing.");
                    smsBody = template.TemplateBody;
                }
            }
            var result = new GuestDistributionResult { Selected = guestIds.Count };
            foreach (long id in guestIds)
            {
                using (var db = new SLTEntities())
                {
                    var guest = await db.Guests.FirstOrDefaultAsync(g => g.Id == id && !g.IsDeleted);
                    if (guest == null) { result.Skipped++; continue; }
                    if (input.SendEmail && (input.ForceResend || !guest.EmailSent))
                    {
                        bool accepted = false;
                        string error = null;
                        try
                        {
                            // First invitation contains only portal link, no passcode.
                            accepted = await new EmailTemplateService().SendAsync("GUEST_INVITATION", guest.Email, guest.FullName, null);
                            if (!accepted) error = "Email API did not accept request";
                        }
                        catch (Exception ex) { error = ex.GetType().Name; }
                        db.Set<GuestMessageDelivery>().Add(new GuestMessageDelivery {
                            GuestId = id, Channel = "Email", TemplateKey = "GUEST_INVITATION",
                            Status = accepted ? "Accepted" : "Failed", AttemptedAt = EgyptDateTimeService.Now,
                            CompletedAt = EgyptDateTimeService.Now, ErrorMessage = error, CreatedBy = actor });
                        if (accepted) { guest.EmailSent = true; result.EmailAccepted++; }
                        else result.Errors.Add("Guest #" + id + " email was not accepted: " + error);
                    }
                    if (input.SendSms && (input.ForceResend || !guest.SmsSent))
                    {
                        bool accepted = false;
                        string error = null;
                        try
                        {
                            if (string.IsNullOrWhiteSpace(guest.PhoneNumber)) throw new ArgumentException("Missing phone number");
                            string login = ConfigurationManager.AppSettings["SLTGuestLoginUrl"];
                            if (string.IsNullOrWhiteSpace(login)) throw new InvalidOperationException("SLTGuestLoginUrl is missing");
                            string body = smsBody.Replace("{FullName}", guest.FullName ?? "")
                                .Replace("{LoginUrl}", login).Replace("{WalletUrl}", guest.WalletUrl ?? "");
                            accepted = await new SmsService().SendSmsAsync(guest.PhoneNumber, body);
                            if (!accepted) error = "SMS API did not accept request";
                        }
                        catch (Exception ex) { error = ex.GetType().Name; }
                        db.Set<GuestMessageDelivery>().Add(new GuestMessageDelivery {
                            GuestId = id, Channel = "Sms", TemplateKey = "GUEST_INVITATION_SMS",
                            Status = accepted ? "Accepted" : "Failed", AttemptedAt = EgyptDateTimeService.Now,
                            CompletedAt = EgyptDateTimeService.Now, ErrorMessage = error, CreatedBy = actor });
                        if (accepted) { guest.SmsSent = true; result.SmsAccepted++; }
                        else result.Errors.Add("Guest #" + id + " SMS was not accepted: " + error);
                    }
                    guest.ModifiedBy = actor;
                    guest.RetentionDateModified = EgyptDateTimeService.Now;
                    await db.SaveChangesAsync();
                }
            }
            return result;
        }

        public async Task<List<object>> GetTemplatesAsync()
        {
            using (var db = new SLTEntities())
            {
                var list = await db.Set<SmsTemplate>().AsNoTracking().OrderBy(t => t.TemplateKey).ToListAsync();
                return list.Select(t => (object)new { t.Id, t.TemplateKey, t.TemplateBody, t.IsActive,
                    t.CreatedBy, t.RetentionDateCreated, t.ModifiedBy, t.RetentionDateModified }).ToList();
            }
        }
        public async Task SaveTemplateAsync(string key, string body, bool isActive, string actor)
        {
            key = Normalize(key);
            string[] accepted = { "GUEST_WELCOME_SMS", "GUEST_INVITATION_SMS", "GUEST_PASSCODE_SMS",
                "AIRPORT_TRANSFER_ALERT_SMS", "GUEST_CHECKIN_ALERT_SMS", "GUEST_CHECKOUT_ALERT_SMS",
                "EVENT_DRIVER_ALERT_SMS", "EVENT_REMINDER_SMS" };
            if (!accepted.Contains(key, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Unsupported template key.");
            if (string.IsNullOrWhiteSpace(body) || body.Length > 2000) throw new ArgumentException("Template must be 1-2000 characters.");
            if (key == "GUEST_INVITATION_SMS" && body.IndexOf("{Passcode}", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new ArgumentException("Initial invitation SMS must not include a passcode.");
            if (key == "GUEST_INVITATION_SMS" && body.IndexOf("{LoginUrl}", StringComparison.OrdinalIgnoreCase) < 0)
                throw new ArgumentException("Invitation SMS must contain {LoginUrl}.");
            using (var db = new SLTEntities())
            {
                var template = await db.Set<SmsTemplate>().FirstOrDefaultAsync(t => t.TemplateKey == key);
                if (template == null)
                {
                    template = new SmsTemplate { TemplateKey = key, CreatedBy = actor,
                        RetentionDateCreated = EgyptDateTimeService.Now };
                    db.Set<SmsTemplate>().Add(template);
                }
                template.TemplateBody = body;
                template.IsActive = isActive;
                template.ModifiedBy = actor;
                template.RetentionDateModified = EgyptDateTimeService.Now;
                await db.SaveChangesAsync();
            }
        }
    }
}
