
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using SLTProject.Data;

namespace SLTProject.Services
{
    public class CustomizedResponsesService
    {
        // ============================================================
        // FORMATTING
        // ============================================================

        private static string FormatTime(object value)
        {
            if (value == null)
                return null;

            if (value is TimeSpan)
            {
                return ((TimeSpan)value).ToString(
                    @"hh\:mm",
                    CultureInfo.InvariantCulture);
            }

            if (value is DateTime)
            {
                return ((DateTime)value).ToString(
                    "HH:mm",
                    CultureInfo.InvariantCulture);
            }

            return Convert.ToString(
                value,
                CultureInfo.InvariantCulture);
        }

        private static string NormalizeEmail(string email)
        {
            return (email ?? "")
                .Trim()
                .ToLowerInvariant();
        }

        private static string NormalizeIconName(string name)
        {
            return (name ?? "").Trim();
        }

        // ============================================================
        // ICON RESPONSE MODEL
        // ============================================================

        public sealed class EventBlurbIconResponse
        {
            public string Name { get; set; }

            public string Shape { get; set; }

            public string SVG { get; set; }
        }

        // ============================================================
        // GET COMBINED EVENTS BY DATE
        // ============================================================

        public async Task<List<Dictionary<string, object>>>
            GetCombinedEventsAsync(string guestEmail)
        {
            string email = NormalizeEmail(guestEmail);

            using (var db = new SLTEntities())
            {
                // ====================================================
                // 1. GET ALL ACTIVE PHASES
                // ====================================================

                var phases = await db.Phases
                    .AsNoTracking()
                    .Where(x => !x.IsDeleted)
                    .OrderBy(x => x.Date)
                    .ThenBy(x => x.Id)
                    .ToListAsync();

                // ====================================================
                // 2. GET ALL ACTIVE DAY SCHEDULE EVENTS
                // ====================================================

                var events = await db.DaySchedules
                    .AsNoTracking()
                    .Where(x => !x.IsDeleted)
                    .OrderBy(x => x.Date)
                    .ThenBy(x => x.StartTime)
                    .ThenBy(x => x.Id)
                    .ToListAsync();

                // ====================================================
                // 3. GET DRIVERS ASSIGNED TO CURRENT GUEST
                // ====================================================

                var guestDrivers = new List<GuestEventDriver>();

                if (!string.IsNullOrWhiteSpace(email))
                {
                    guestDrivers = await db.GuestEventDrivers
                        .AsNoTracking()
                        .Where(x =>
                            !x.IsDeleted &&
                            x.Email == email)
                        .OrderBy(x => x.Id)
                        .ToListAsync();
                }

                // ====================================================
                // 4. GET ACTIVE BLURB ICONS
                //
                // IMPORTANT:
                // Read actual Name, Shape and SVG columns.
                //
                // The icon name is stored in:
                // DaySchedules.BlurIconName
                //
                // Do not use DaySchedules.ItemType for icon lookup.
                // ====================================================

                var icons = await db.BlurIcons
                    .AsNoTracking()
                    .Where(x => !x.IsDeleted)
                    .OrderBy(x => x.Id)
                    .Select(x => new
                    {
                        x.Name,
                        x.Shape,
                        x.SVG
                    })
                    .ToListAsync();

                // ====================================================
                // 5. CREATE ICON LOOKUP BY NAME
                //
                // Case insensitive and whitespace tolerant.
                // ====================================================

                var iconByName =
                    new Dictionary<string, EventBlurbIconResponse>(
                        StringComparer.OrdinalIgnoreCase);

                foreach (var icon in icons)
                {
                    string key = NormalizeIconName(icon.Name);

                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    if (iconByName.ContainsKey(key))
                        continue;

                    iconByName.Add(
                        key,
                        new EventBlurbIconResponse
                        {
                            Name = icon.Name,
                            Shape = icon.Shape,
                            SVG = icon.SVG
                        });
                }

                // ====================================================
                // 6. CREATE PHASE LOOKUP BY DATE
                //
                // Date comparisons ignore the time component.
                //
                // Example:
                // Phase.Date = 2026-11-10 00:00:00
                // Event.Date = 2026-11-10 08:00:00
                //
                // Both match 2026-11-10.
                // ====================================================

                var phaseByDate = phases
                    .GroupBy(x => x.Date.Date)
                    .ToDictionary(
                        x => x.Key,
                        x => x.First());

                // ====================================================
                // 7. CREATE DRIVER LOOKUP BY SCHEDULE ID
                // ====================================================

                var driverByEventId = guestDrivers
                    .GroupBy(x => x.DayScheduleId)
                    .ToDictionary(
                        x => x.Key,
                        x => x.First());

                // ====================================================
                // 8. GROUP EVENTS BY CALENDAR DATE
                // ====================================================

                var eventsByDate = events
                    .GroupBy(x => x.Date.Date)
                    .ToDictionary(
                        x => x.Key,
                        x => x.ToList());

                // ====================================================
                // 9. COLLECT ALL DATES
                //
                // Return dates containing:
                // - A phase
                // - One or more scheduled events
                // ====================================================

                var allDates = phaseByDate.Keys
                    .Union(eventsByDate.Keys)
                    .OrderBy(x => x)
                    .ToList();

                var results =
                    new List<Dictionary<string, object>>();

                // ====================================================
                // 10. BUILD RESPONSE FOR EACH DATE
                // ====================================================

                foreach (var eventDate in allDates)
                {
                    Phase phase;

                    phaseByDate.TryGetValue(
                        eventDate,
                        out phase);

                    var row =
                        new Dictionary<string, object>();

                    // ----------------------------------------------
                    // EVENT DATE
                    // ----------------------------------------------

                    row["EventDate"] =
     eventDate.ToString(
         "dd/MM/yyyy",
         CultureInfo.InvariantCulture);

                    row["EventDay"] =
                        eventDate.ToString(
                            "dddd",
                            CultureInfo.InvariantCulture);

                    // ----------------------------------------------
                    // PHASE INFORMATION
                    //
                    // All values come from Phases table
                    // using the matching calendar date.
                    // ----------------------------------------------

                    row["PhaseTitle"] =
                        phase != null
                            ? phase.Title
                            : null;

                    row["EventWelcomeMessage"] =
                        phase != null
                            ? phase.EventWelcomeMessage
                            : null;

                    row["EventSloganMessage"] =
                        phase != null
                            ? phase.EventSloganMessage
                            : null;

                    // ----------------------------------------------
                    // GET SCHEDULED EVENTS FOR THE DATE
                    // ----------------------------------------------

                    List<DaySchedule> dailyEvents;

                    if (!eventsByDate.TryGetValue(
                        eventDate,
                        out dailyEvents))
                    {
                        dailyEvents = new List<DaySchedule>();
                    }

                    // ----------------------------------------------
                    // BUILD EACH EVENT
                    // ----------------------------------------------

                    int eventNumber = 1;

                    foreach (var item in dailyEvents)
                    {
                        string prefix = "Event" + eventNumber;

                        // ==========================================
                        // A. FIND GUEST-SPECIFIC DRIVER
                        // ==========================================

                        GuestEventDriver driver;

                        driverByEventId.TryGetValue(
                            item.Id,
                            out driver);

                        bool hasPhone =
                            driver != null &&
                            !string.IsNullOrWhiteSpace(
                                driver.DriverPhone);

                        bool hasWhatsApp =
                            hasPhone &&
                            driver.WhatsApp;

                        // ==========================================
                        // B. FIND THE FULL BLURB ICON
                        //
                        // Lookup using BlurIconName, not ItemType.
                        // ==========================================

                        EventBlurbIconResponse blurbIcon = null;

                        string savedIconName =
                            NormalizeIconName(
                                item.BlurIconName);

                        if (!string.IsNullOrWhiteSpace(
                            savedIconName))
                        {
                            EventBlurbIconResponse matchedIcon;

                            if (iconByName.TryGetValue(
                                savedIconName,
                                out matchedIcon))
                            {
                                blurbIcon = matchedIcon;
                            }
                        }

                        // ==========================================
                        // C. EVENT DETAILS
                        // ==========================================

                        row[prefix] = item.Title;

                        // Keep existing icon name field.
                        // Return the saved name even if its icon
                        // record is currently missing or deleted.

                        row[prefix + "BlurbIconName"] =
                            string.IsNullOrWhiteSpace(savedIconName)
                                ? null
                                : savedIconName;

                        // New full icon object:
                        // {
                        //   Name: "...",
                        //   Shape: "...",
                        //   SVG: "..."
                        // }

                        row[prefix + "BlurbIcon"] = blurbIcon;

                        // Driver availability

                        row[prefix + "HasPhone"] = hasPhone;

                        row[prefix + "HasWhatsApp"] = hasWhatsApp;

                        // Schedule times

                        row[prefix + "StartTime"] =
                            FormatTime(item.StartTime);

                        row[prefix + "EndTime"] =
                            FormatTime(item.EndTime);

                        // Description

                        row[prefix + "Description"] =
                            item.ShortNote;

                        // Location information

                        row[prefix + "LocationName"] =
                            item.LocationName;

                        row[prefix + "LocationURL"] =
                            item.MapsLink;

                        eventNumber++;
                    }

                    results.Add(row);
                }

                return results;
            }
        }
    }
}
