using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OfflineFirstAccess.Models;

namespace RecoTool.Services
{
    // Partial: stable CRC32 computation over entity business columns.
    // Used by ApplyEntitiesBatchAsync (OfflineFirstService.BatchOps.cs) to detect
    // no-op updates on AMBRE rows without round-tripping every column individually.
    public partial class OfflineFirstService
    {
        /// <summary>
        /// Calcule un CRC32 stable à partir des colonnes ordonnées (normalisées) d'une entité.
        /// </summary>
        private static uint ComputeCrc32ForEntity(Entity entity, List<string> orderedCols)
        {
            uint crc = 0u;
            var enc = Encoding.UTF8;
            var sep = new byte[] { 0x1F }; // Unit Separator pour délimiter
            bool first = true;
            foreach (var col in orderedCols)
            {
                if (!first)
                    crc = Crc32Append(crc, sep, 0, sep.Length);
                first = false;
                entity.Properties.TryGetValue(col, out var val);
                var norm = NormalizeForCrc(val);
                var bytes = enc.GetBytes(norm);
                crc = Crc32Append(crc, bytes, 0, bytes.Length);
            }
            return crc;
        }

        private static string NormalizeForCrc(object value)
        {
            if (value == null || value == DBNull.Value) return string.Empty;
            switch (value)
            {
                case string s:
                    return s?.Trim() ?? string.Empty;
                case DateTime dt:
                    return dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                case bool b:
                    return b ? "1" : "0";
                case decimal dec:
                    return dec.ToString(CultureInfo.InvariantCulture);
                case double d:
                    return d.ToString("G17", CultureInfo.InvariantCulture);
                case float f:
                    return f.ToString("G9", CultureInfo.InvariantCulture);
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        private static readonly uint[] _crc32Table = BuildCrc32Table();

        private static uint Crc32Append(uint crc, byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                byte b = buffer[offset + i];
                uint idx = (crc ^ b) & 0xFFu;
                crc = (crc >> 8) ^ _crc32Table[idx];
            }
            return crc;
        }

        private static uint[] BuildCrc32Table()
        {
            const uint poly = 0xEDB88320u;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((c & 1) != 0)
                        c = poly ^ (c >> 1);
                    else
                        c >>= 1;
                }
                table[i] = c;
            }
            return table;
        }
    }
}
