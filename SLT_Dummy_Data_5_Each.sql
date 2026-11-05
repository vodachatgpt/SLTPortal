/*
 SLT DEMO DATA — 5 Guests + 5 Airport Transfers + 5 Day Schedules + 5 Event Drivers
 Target: SQL Server database containing dbo.Guests, dbo.AirportTransfers,
         dbo.DaySchedules, dbo.GuestEventDrivers.

 SAFE FOR TEST/STAGING ONLY. Run against a backed-up test database.
 Idempotent for its own sample keys; no DELETE/TRUNCATE.
 Dates: Egypt-local audit timestamps, using SQL Server AT TIME ZONE.
 SAMPLE GUESTS ARE NOT LOGIN-READY: PasscodeHash stores random bytes, NOT
 a HMAC using your configured pepper. For actual login create/reset through API.
 Requires SQL Server 2016+ and Guest.PhoneNumber and DaySchedule.BlurIconName
 schema upgrade applied. If those columns are missing, first run the migration.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.Guests', N'U') IS NULL
 OR OBJECT_ID(N'dbo.AirportTransfers', N'U') IS NULL
 OR OBJECT_ID(N'dbo.DaySchedules', N'U') IS NULL
 OR OBJECT_ID(N'dbo.GuestEventDrivers', N'U') IS NULL
    THROW 51000, 'Missing SLT tables. Check the database selection.', 1;

IF COL_LENGTH('dbo.Guests', 'PhoneNumber') IS NULL
    THROW 51001, 'Missing Guests.PhoneNumber. Apply the schema migration first.', 1;

IF COL_LENGTH('dbo.DaySchedules', 'BlurIconName') IS NULL
    THROW 51002, 'Missing DaySchedules.BlurIconName. Apply the schema migration first.', 1;

DECLARE @NowEgypt datetime2(3) = CAST(
    SYSUTCDATETIME() AT TIME ZONE 'UTC'
                     AT TIME ZONE 'Egypt Standard Time'
    AS datetime2(3));
DECLARE @Actor nvarchar(250) = N'SLT_DEMO_SEED';

DECLARE @Guests TABLE (
    Num int PRIMARY KEY, Email nvarchar(320), FullName nvarchar(250),
    Phone nvarchar(30), Hotel nvarchar(250), Arrival date, Departure date);
INSERT INTO @Guests(Num,Email,FullName,Phone,Hotel,Arrival,Departure) VALUES
 (1,N'slt.demo01@example.test',N'SLT Demo Guest 01',N'+201001234501',N'Demo Cairo Hotel', '2026-11-09','2026-11-16'),
 (2,N'slt.demo02@example.test',N'SLT Demo Guest 02',N'+201001234502',N'Demo Nile Hotel',  '2026-11-09','2026-11-16'),
 (3,N'slt.demo03@example.test',N'SLT Demo Guest 03',N'+201001234503',N'Demo Plaza Hotel', '2026-11-10','2026-11-17'),
 (4,N'slt.demo04@example.test',N'SLT Demo Guest 04',N'+201001234504',N'Demo Cairo Hotel', '2026-11-10','2026-11-17'),
 (5,N'slt.demo05@example.test',N'SLT Demo Guest 05',N'+201001234505',N'Demo Nile Hotel',  '2026-11-11','2026-11-18');

DECLARE @Events TABLE (
    Num int PRIMARY KEY, [Date] date, ItemType nvarchar(100),
    Title nvarchar(250), Starts time(0), Ends time(0), [Location] nvarchar(250));
INSERT INTO @Events(Num,[Date],ItemType,Title,Starts,Ends,[Location]) VALUES
 (1,'2026-11-10',N'Workshop',N'SLT Demo Event 01 - Welcome','09:00','10:30',N'Demo Hall A'),
 (2,'2026-11-11',N'Conference',N'SLT Demo Event 02 - Strategy','10:00','12:00',N'Demo Hall B'),
 (3,'2026-11-12',N'Team Building',N'SLT Demo Event 03 - Teamwork','13:00','15:00',N'Demo Garden'),
 (4,'2026-11-13',N'Dinner',N'SLT Demo Event 04 - Networking','19:00','21:00',N'Demo Terrace'),
 (5,'2026-11-14',N'Workshop',N'SLT Demo Event 05 - Closing','11:00','12:30',N'Demo Hall A');

BEGIN TRY
    BEGIN TRANSACTION;

    -- 1) Guests. Random hashes prevent known, functioning sample passcodes.
    INSERT INTO dbo.Guests
       (FullName,Email,PhoneNumber,ArrivalDate,ArrivalFlight,LandingTime,
        DepartureDate,DepartureFlight,DepartureTime,HotelName,
        CheckInDate,CheckInTime,CheckOutDate,CheckOutTime,
        PasscodeHash,PasscodeLast4,FirstLoginAt,SessionTokenHash,
        SessionExpiresAt,RetentionDateCreated,CreatedBy,IsDeleted,TourRegister)
    SELECT g.FullName,g.Email,g.Phone,g.Arrival,N'MS' + CAST(100+g.Num AS nvarchar(10)),
           CAST('09:15:00' AS time),g.Departure,N'MS' + CAST(200+g.Num AS nvarchar(10)),
           CAST('18:30:00' AS time),g.Hotel,g.Arrival,
           CAST('14:00:00' AS time),g.Departure,CAST('12:00:00' AS time),
           CRYPT_GEN_RANDOM(32),N'----',NULL,NULL,NULL,@NowEgypt,@Actor,0,0
    FROM @Guests g
    WHERE NOT EXISTS (SELECT 1 FROM dbo.Guests x WHERE x.Email=g.Email);

    -- 2) Five day-event schedules (ItemType is free text).
    INSERT INTO dbo.DaySchedules
       ([Date],ItemType,Title,StartTime,EndTime,ShortNote,DressCode,
        LocationName,MapsLink,BlurIconName,CreatedAt,CreatedBy,IsDeleted,RetentionDateCreated)
    SELECT e.[Date],e.ItemType,e.Title,e.Starts,e.Ends,
           N'SLT demo schedule; safe to edit or delete.',N'Smart casual',
           e.[Location],NULL,NULL,@NowEgypt,@Actor,0,@NowEgypt
    FROM @Events e
    WHERE NOT EXISTS (SELECT 1 FROM dbo.DaySchedules d WHERE d.Title=e.Title);

    -- 3) One airport transfer per sample guest (mixed Pickup/Drop-off).
    INSERT INTO dbo.AirportTransfers
       (Email,Type,[Date],DriverName,DriverPhone,WhatsApp,
        CreatedAt,CreatedBy,IsDeleted,RetentionDateCreated)
    SELECT g.Email,CASE WHEN g.Num % 2=1 THEN N'Pickup' ELSE N'Drop-off' END,
           CASE WHEN g.Num % 2=1 THEN g.Arrival ELSE g.Departure END,
           N'Demo Airport Driver ' + RIGHT(N'0'+CAST(g.Num AS nvarchar(2)),2),
           N'+20105550100'+CAST(g.Num AS nvarchar(1)),1,
           @NowEgypt,@Actor,0,@NowEgypt
    FROM @Guests g
    WHERE NOT EXISTS (
      SELECT 1 FROM dbo.AirportTransfers a
      WHERE a.Email=g.Email AND a.Type=CASE WHEN g.Num % 2=1 THEN N'Pickup' ELSE N'Drop-off' END
        AND a.IsDeleted=0
    );

    -- 4) Five event driver assignments, tied to exact actual DaySchedule IDs.
    INSERT INTO dbo.GuestEventDrivers
       (Email,DayScheduleId,DriverName,DriverPhone,WhatsApp,
        CreatedAt,CreatedBy,IsDeleted,RetentionDateCreated)
    SELECT g.Email,d.Id,
           N'Demo Event Driver ' + RIGHT(N'0'+CAST(g.Num AS nvarchar(2)),2),
           N'+20105550200'+CAST(g.Num AS nvarchar(1)),1,
           @NowEgypt,@Actor,0,@NowEgypt
    FROM @Guests g
    JOIN @Events e ON e.Num=g.Num
    CROSS APPLY (
        SELECT TOP (1) ds.Id
        FROM dbo.DaySchedules ds
        WHERE ds.Title=e.Title AND ds.IsDeleted=0
        ORDER BY ds.Id
    ) d
    WHERE NOT EXISTS (
      SELECT 1 FROM dbo.GuestEventDrivers x
      WHERE x.Email=g.Email AND x.DayScheduleId=d.Id AND x.IsDeleted=0
    );

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

-- Preview only the seeded example data.
SELECT N'Guests' AS [Table],COUNT(*) AS SampleRecords FROM dbo.Guests WHERE Email LIKE N'slt.demo0%@example.test'
UNION ALL SELECT N'AirportTransfers',COUNT(*) FROM dbo.AirportTransfers WHERE Email LIKE N'slt.demo0%@example.test' AND IsDeleted=0
UNION ALL SELECT N'DaySchedules',COUNT(*) FROM dbo.DaySchedules WHERE Title LIKE N'SLT Demo Event 0%' AND IsDeleted=0
UNION ALL SELECT N'GuestEventDrivers',COUNT(*) FROM dbo.GuestEventDrivers WHERE Email LIKE N'slt.demo0%@example.test' AND IsDeleted=0;

SELECT g.Id,g.FullName,g.Email,g.PhoneNumber,g.HotelName FROM dbo.Guests g WHERE g.Email LIKE N'slt.demo0%@example.test' ORDER BY g.Email;
SELECT d.Id,d.Title,d.[Date],d.ItemType FROM dbo.DaySchedules d WHERE d.Title LIKE N'SLT Demo Event 0%' ORDER BY d.[Date];
SELECT t.Id,t.Email,t.Type,t.[Date],t.DriverName FROM dbo.AirportTransfers t WHERE t.Email LIKE N'slt.demo0%@example.test' ORDER BY t.Email;
SELECT e.Id,e.Email,e.DayScheduleId,s.Title,e.DriverName FROM dbo.GuestEventDrivers e JOIN dbo.DaySchedules s ON s.Id=e.DayScheduleId WHERE e.Email LIKE N'slt.demo0%@example.test' ORDER BY e.Email;
