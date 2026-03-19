using System;
using System.IO;

namespace RecoTool.Infrastructure.DataAccess
{
    /// <summary>
    /// Centralized ACE OLE DB connection string helpers.
    /// </summary>
    public static class DbConn
    {
        public static string AceConn(string path)
            => $"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={path};";

        public static string AceConnNetwork(string path)
            => $"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={path};Jet OLEDB:Database Locking Mode=1;Mode=Share Deny None;";

        /// <summary>
        /// Resolves a file path or connection string into a valid OleDb connection string.
        /// Accepts either a full connection string (contains '=') or a file path (.accdb/.mdb).
        /// </summary>
        public static string ResolveConnectionString(string pathOrConnectionString)
        {
            if (string.IsNullOrWhiteSpace(pathOrConnectionString))
                throw new ArgumentNullException(nameof(pathOrConnectionString));

            // Already a connection string
            if (pathOrConnectionString.Contains("="))
                return pathOrConnectionString;

            // File path — build connection string based on extension
            if (File.Exists(pathOrConnectionString))
            {
                var ext = Path.GetExtension(pathOrConnectionString)?.ToLowerInvariant();
                if (ext == ".mdb")
                    return $"Provider=Microsoft.Jet.OLEDB.4.0;Data Source={pathOrConnectionString};Persist Security Info=False;";
                // .accdb or any other extension → ACE 12.0
                return $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={pathOrConnectionString};Persist Security Info=False;";
            }

            // Assume it's a connection string if file doesn't exist
            return pathOrConnectionString;
        }
    }
}
