
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using SLTProject.Data;

namespace SLTProject.Services
{
    public class CustomizedGuestResponsesService
    {
        // ============================================================
        // EMAIL NORMALIZATION
        // ============================================================

        private static string NormalizeEmail(string email)
        {
            return (email ?? "")
                .Trim()
                .ToLowerInvariant();
        }

        // ============================================================
        // FULL NAME SPLITTING
        // ============================================================

        private static string GetFirstName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return null;

            string[] parts = fullName.Trim()
                .Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries);

            return parts.Length > 0
                ? parts[0]
                : null;
        }

        private static string GetLastName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                return null;

            string[] parts = fullName.Trim()
                .Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length <= 1)
                return null;

            return string.Join(" ", parts.Skip(1));
        }

        // ============================================================
        // DATE FORMATTING
        //
        // Day / Month / Year
        //
        // Examples:
        // 05/11/2026
        // 05/11/2026 14:27:41
        //
        // These functions format output only.
        // They do not change stored SQL timestamps.
        // ============================================================

        private static string FormatDate(DateTime? value)
        {
            if (!value.HasValue)
                return null;

            return value.Value.ToString(
                "dd/MM/yyyy",
                CultureInfo.InvariantCulture);
        }

        private static string FormatDateTime(DateTime? value)
        {
            if (!value.HasValue)
                return null;

            return value.Value.ToString(
                "dd/MM/yyyy HH:mm:ss",
                CultureInfo.InvariantCulture);
        }

        // ============================================================
        // DATE + TIME COMBINATION
        // ============================================================

        private static DateTime? CombineDateTime(
            DateTime? date,
            TimeSpan? time)
        {
            if (!date.HasValue)
                return null;

            // Preserve the date when the time is missing.
            // Use midnight as the default time.
            return date.Value.Date.Add(
                time ?? TimeSpan.Zero);
        }

        // ============================================================
        // GET GUESTS WITH ALL DRIVER ASSIGNMENTS
        // ============================================================

        public async Task<List<Dictionary<string, object>>>
            GetGuestsAsync(
                string guestEmail,
                string qrUrlPrefix)
        {
            string email = NormalizeEmail(guestEmail);

            using (var db = new SLTEntities())
            {
                // ====================================================
                // 1. LOAD ACTIVE GUESTS
                // ====================================================

                var guestQuery = db.Guests
                    .AsNoTracking()
                    .Where(x => !x.IsDeleted);

                if (!string.IsNullOrWhiteSpace(email))
                {
                    guestQuery = guestQuery.Where(
                        x => x.Email == email);
                }

                // Select required fields only.
                // Do not load QR image binary data.

                var guests = await guestQuery
                    .OrderBy(x => x.FullName)
                    .ThenBy(x => x.Id)
                    .Select(x => new
                    {
                        x.Id,
                        x.FullName,
                        x.Email,

                        // Actual phone number from Guests.
                        x.PhoneNumber,

                        // First successful guest login.
                        x.FirstLoginAt,

                        // Hotel check-in/check-out.
                        x.CheckInDate,
                        x.CheckInTime,
                        x.CheckOutDate,
                        x.CheckOutTime,

                        // Tour registration status.
                        x.TourRegister,

                        // QR image availability.
                        HasQR = x.QRCodeImage != null
                    })
                    .ToListAsync();

                if (guests.Count == 0)
                {
                    return new List<
                        Dictionary<string, object>>();
                }

                // ====================================================
                // 2. LOAD ACTIVE DAY SCHEDULES
                // ====================================================

                var schedules = await db.DaySchedules
                    .AsNoTracking()
                    .Where(x => !x.IsDeleted)
                    .OrderBy(x => x.Date)
                    .ThenBy(x => x.StartTime)
                    .ThenBy(x => x.Id)
                    .Select(x => new
                    {
                        x.Id,
                        x.Date,
                        x.StartTime
                    })
                    .ToListAsync();

                // ====================================================
                // 3. GROUP SCHEDULES BY DAY
                // ====================================================

                var days = schedules
                    .GroupBy(x => x.Date.Date)
                    .OrderBy(x => x.Key)
                    .Select(x => x.ToList())
                    .ToList();

                // ====================================================
                // 4. LOAD ACTIVE AIRPORT TRANSFERS
                // ====================================================

                var airportQuery = db.AirportTransfers
                    .AsNoTracking()
                    .Where(x => !x.IsDeleted);

                if (!string.IsNullOrWhiteSpace(email))
                {
                    airportQuery = airportQuery.Where(
                        x => x.Email == email);
                }

                var airportTransfers = await airportQuery
                    .ToListAsync();

                // ====================================================
                // 5. LOAD ACTIVE EVENT DRIVERS
                // ====================================================

                var eventDriversQuery = db.GuestEventDrivers
                    .AsNoTracking()
                    .Where(x => !x.IsDeleted);

                if (!string.IsNullOrWhiteSpace(email))
                {
                    eventDriversQuery = eventDriversQuery.Where(
                        x => x.Email == email);
                }

                var eventDrivers = await eventDriversQuery
                    .ToListAsync();

                // ====================================================
                // 6. AIRPORT TRANSFER LOOKUP
                // ====================================================

                var airportLookup = airportTransfers
                    .GroupBy(x =>
                        NormalizeEmail(x.Email) + "|" +
                        (x.Type ?? "")
                            .Trim()
                            .ToLowerInvariant())
                    .ToDictionary(
                        x => x.Key,
                        x => x.OrderBy(y => y.Id).First());

                // ====================================================
                // 7. EVENT DRIVER LOOKUP
                // ====================================================

                var eventDriverLookup = eventDrivers
                    .GroupBy(x =>
                        NormalizeEmail(x.Email) + "|" +
                        x.DayScheduleId.ToString(
                            CultureInfo.InvariantCulture))
                    .ToDictionary(
                        x => x.Key,
                        x => x.OrderBy(y => y.Id).First());

                // ====================================================
                // 8. BUILD CUSTOMIZED RESPONSE
                // ====================================================

                var results =
                    new List<Dictionary<string, object>>();

                foreach (var guest in guests)
                {
                    string guestKey =
                        NormalizeEmail(guest.Email);

                    var row =
                        new Dictionary<string, object>();

                    // ================================================
                    // PERSONAL INFORMATION
                    // ================================================

                    row["FirstName"] =
                        GetFirstName(guest.FullName);

                    row["LastName"] =
                        GetLastName(guest.FullName);

                    row["FullName"] =
                        guest.FullName;

                    // FIX 1:
                    // Read PhoneNumber from Guests.
                    // Keep MobileNumber for frontend compatibility.

                    row["MobileNumber"] =
                        guest.PhoneNumber;

                    row["Email"] =
                        guest.Email;

                    // FIX 2:
                    // FirstLogin formatted Day/Month/Year.

                    row["FirstLogin"] =
                        FormatDateTime(
                            guest.FirstLoginAt);

                    // FIX 3:
                    // Check-in formatted Day/Month/Year.

                    row["CheckInDateTime"] =
                        FormatDateTime(
                            CombineDateTime(
                                guest.CheckInDate,
                                guest.CheckInTime));

                    // FIX 4:
                    // Check-out formatted Day/Month/Year.

                    row["CheckOutDateTime"] =
                        FormatDateTime(
                            CombineDateTime(
                                guest.CheckOutDate,
                                guest.CheckOutTime));

                    // FIX 5:
                    // Actual tour registration status.
                    // Returns true or false instead of null.

                    row["TourRegisteration"] =
                        guest.TourRegister;

                    // ================================================
                    // QR IMAGE
                    // ================================================

                    row["QRUrl"] =
                        guest.HasQR &&
                        !string.IsNullOrWhiteSpace(qrUrlPrefix)
                            ? qrUrlPrefix + guest.Id + "/qr"
                            : null;

                    // ================================================
                    // AIRPORT PICKUP
                    // ================================================

                    AirportTransfer pickup = null;

                    airportLookup.TryGetValue(
                        guestKey + "|pickup",
                        out pickup);

                    row["AirportPickupDriverName"] =
                        pickup == null
                            ? null
                            : pickup.DriverName;

                    row["AirportPickupDriverNumber"] =
                        pickup == null
                            ? null
                            : pickup.DriverPhone;

                    // ================================================
                    // AIRPORT DROP-OFF
                    // ================================================

                    AirportTransfer dropOff = null;

                    airportLookup.TryGetValue(
                        guestKey + "|drop-off",
                        out dropOff);

                    row["AirportDropOffDriverName"] =
                        dropOff == null
                            ? null
                            : dropOff.DriverName;

                    row["AirportDropOffDriverNumber"] =
                        dropOff == null
                            ? null
                            : dropOff.DriverPhone;

                    // ================================================
                    // DAILY EVENT DRIVERS
                    //
                    // Maintain the original API structure:
                    //
                    // Day1Event1DriverName
                    // Day1Event1DriverNumber
                    // ...
                    //
                    // Minimum 4 days, 10 events per day.
                    // Expand automatically when necessary.
                    // ================================================

                    int totalDays = Math.Max(
                        4,
                        days.Count);

                    for (int dayIndex = 0;
                         dayIndex < totalDays;
                         dayIndex++)
                    {
                        bool hasDay =
                            dayIndex < days.Count;

                        int totalEvents = hasDay
                            ? Math.Max(
                                10,
                                days[dayIndex].Count)
                            : 10;

                        for (int eventIndex = 0;
                             eventIndex < totalEvents;
                             eventIndex++)
                        {
                            string prefix =
                                "Day" + (dayIndex + 1) +
                                "Event" + (eventIndex + 1);

                            string driverNameKey =
                                prefix + "DriverName";

                            string driverNumberKey =
                                prefix + "DriverNumber";

                            // Default to null when no assigned
                            // driver exists for the event.

                            row[driverNameKey] = null;

                            row[driverNumberKey] = null;

                            if (!hasDay ||
                                eventIndex >=
                                days[dayIndex].Count)
                            {
                                continue;
                            }

                            var schedule =
                                days[dayIndex][eventIndex];

                            string lookupKey =
                                guestKey + "|" +
                                schedule.Id.ToString(
                                    CultureInfo.InvariantCulture);

                            GuestEventDriver driver = null;

                            if (!eventDriverLookup.TryGetValue(
                                lookupKey,
                                out driver))
                            {
                                continue;
                            }

                            row[driverNameKey] =
                                driver.DriverName;

                            row[driverNumberKey] =
                                driver.DriverPhone;
                        }
                    }

                    results.Add(row);
                }

                return results;
            }
        }
    }
}
