using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TechEquipments
{
    public sealed class PgDbService : IDbService
    {
        private readonly IDbContextFactory<PgDbContext> _dbFactory;

        public PgDbService(IDbContextFactory<PgDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public async Task<bool> CanConnectAsync(CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            return await db.Database.CanConnectAsync(ct);
        }

        public async Task<IReadOnlyList<OperatorActDTO>> GetOperatorActsAsync(
            DateTime date, string? equipFilter, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var (from, to) = BuildLocalDayRangeUtc(date);

            //var from = date.Date;
            //var to = from.AddDays(1);

            var q = db.OperatorActs
                .AsNoTracking()
                .Where(x => x.Date >= from && x.Date < to);

            if (!string.IsNullOrWhiteSpace(equipFilter))
            {
                var s = equipFilter.Trim();
                // PostgreSQL case-insensitive:
                q = q.Where(x =>
                    (x.Equip != null && EF.Functions.ILike(x.Equip, $"%{s}%")) ||
                    (x.Tag != null && EF.Functions.ILike(x.Tag, $"%{s}%")));
            }

            var list = await q.OrderByDescending(x => x.Date).ToListAsync(ct);

            return list.Select(x => new OperatorActDTO
            {
                Date = x.Date.Date,
                Time = x.Date.ToString(CultureInfo.CurrentCulture.DateTimeFormat.LongTimePattern),
                Type = x.Type,
                Client = x.Client?.Trim(),
                User = x.User?.Trim(),
                Tag = x.Tag?.Trim(),
                Equip = x.Equip?.Trim(),
                Desc = x.Desc?.Trim(),
                OldV = x.OldV?.Trim().ToUpperInvariant(),
                NewV = x.NewV?.Trim().ToUpperInvariant(),
            }).ToList();
        }

        public async Task<IReadOnlyList<AlarmHistoryDTO>> GetAlarmHistoryAsync(
            DateTime date, string? equipFilter, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var (from, to) = BuildLocalDayRangeUtc(date);
            //var from = date.Date;
            //var to = from.AddDays(1);

            // localtimedate может быть DateTimeOffset или DateTime.
            // Если DateTimeOffset — сравнение через диапазон всё равно ок, EF переведёт.
            var q = db.AlarmHistories
                .AsNoTracking()
                .Where(x => x.localtimedate >= from && x.localtimedate < to);

            if (!string.IsNullOrWhiteSpace(equipFilter))
            {
                var s = equipFilter.Trim();
                q = q.Where(x =>
                    x.equipment != null && EF.Functions.ILike(x.equipment, $"%{s}%"));
            }

            var list = await q.OrderByDescending(x => x.localtimedate).ToListAsync(ct);

            return list.Select(x => new AlarmHistoryDTO
            {
                Date = x.localtimedate.Date,
                Time = x.localtimedate.ToString(CultureInfo.CurrentCulture.DateTimeFormat.LongTimePattern),
                Category = x.category?.Trim(),
                User = x.fullname?.Trim(),
                Location = x.userlocation?.Trim(),
                Equipment = x.equipment?.Trim(),
                Item = x.item?.Trim(),
                Comment = x.desc_?.Trim(),
                State = x.logstate?.Trim()
            }).ToList();
        }

        private static (DateTimeOffset fromUtc, DateTimeOffset toUtc) BuildLocalDayRangeUtc(DateTime date)
        {
            var tz = TimeZoneInfo.Local;

            var startLocal = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, tz);
            var endUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal.AddDays(1), tz);

            return (new DateTimeOffset(startUtc, TimeSpan.Zero),
                    new DateTimeOffset(endUtc, TimeSpan.Zero));
        }
    }
}
