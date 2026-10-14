using System;
using System.Collections.Specialized;
using System.Configuration;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;

namespace FaceScan
{
    /// <summary>Settings from FaceScan.exe.config, with the clinic database optionally overridden on the command line.</summary>
    public sealed class AppSettings
    {
        public string ConnectionString { get; private set; }
        public string DatabaseName { get; private set; }
        public string Schema { get; private set; }
        public string PeopleTable { get; private set; }
        public string AppointmentsTable { get; private set; }
        public string FaceDataFolder { get; private set; }
        public string ModelsFolder { get; private set; }
        public double MatchThreshold { get; private set; }
        public double MatchMargin { get; private set; }
        public TimeSpan MinTimeBeforeCheckout { get; private set; }
        public int CameraIndex { get; private set; }

        /// <summary>Path of this clinic's enrolled-faces file; each clinic database gets its own.</summary>
        public string FaceStorePath
        {
            get
            {
                var safeName = DatabaseName;
                foreach (var c in Path.GetInvalidFileNameChars())
                {
                    safeName = safeName.Replace(c, '_');
                }
                return Path.Combine(FaceDataFolder, "faces_" + safeName + ".dat");
            }
        }

        public static AppSettings Load(string[] args)
        {
            var connection = ConfigurationManager.ConnectionStrings["Clinic"];
            if (connection == null || string.IsNullOrWhiteSpace(connection.ConnectionString))
            {
                throw new ConfigurationErrorsException(
                    "FaceScan.exe.config has no connection string named \"Clinic\".");
            }
            return FromValues(connection.ConnectionString, ConfigurationManager.AppSettings, args,
                AppDomain.CurrentDomain.BaseDirectory);
        }

        public static AppSettings FromValues(string connectionString, NameValueCollection values,
            string[] args, string appFolder)
        {
            var database = DatabaseArgument(args) ?? Value(values, "Database", null);

            var defaultDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FaceScan");

            var settings = new AppSettings
            {
                ConnectionString = WithDatabase(connectionString, database),
                Schema = Value(values, "Schema", "dbo"),
                PeopleTable = Value(values, "PeopleTable", "people"),
                AppointmentsTable = Value(values, "AppointmentsTable", "appointments"),
                FaceDataFolder = Environment.ExpandEnvironmentVariables(
                    Value(values, "FaceDataFolder", defaultDataFolder)),
                ModelsFolder = Path.Combine(appFolder, "models"),
                MatchThreshold = Number(values, "MatchThreshold", 0.40),
                MatchMargin = Number(values, "MatchMargin", 0.05),
                MinTimeBeforeCheckout = TimeSpan.FromMinutes(Number(values, "MinMinutesBeforeCheckout", 5)),
                CameraIndex = (int)Number(values, "CameraIndex", 0),
            };
            settings.DatabaseName = new SqlConnectionStringBuilder(settings.ConnectionString).InitialCatalog;
            return settings;
        }

        /// <summary>The connection string with Initial Catalog replaced by <paramref name="database"/> (if given).</summary>
        public static string WithDatabase(string connectionString, string database)
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrWhiteSpace(database))
            {
                builder.InitialCatalog = database.Trim();
            }
            if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            {
                throw new ConfigurationErrorsException(
                    "No clinic database: set Initial Catalog in the connection string, the Database setting, " +
                    "or start FaceScan with /database:<name>.");
            }
            return builder.ConnectionString;
        }

        /// <summary>Accepts /database:name, -database:name, --database name or --database=name.</summary>
        private static string DatabaseArgument(string[] args)
        {
            if (args == null)
            {
                return null;
            }
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i].TrimStart('/', '-');
                if (!arg.StartsWith("database", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var rest = arg.Substring("database".Length);
                if (rest.Length > 0 && (rest[0] == ':' || rest[0] == '='))
                {
                    return rest.Substring(1);
                }
                if (rest.Length == 0 && i + 1 < args.Length)
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        private static string Value(NameValueCollection values, string key, string fallback)
        {
            var value = values == null ? null : values[key];
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static double Number(NameValueCollection values, string key, double fallback)
        {
            var value = Value(values, key, null);
            if (value == null)
            {
                return fallback;
            }
            double result;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                throw new ConfigurationErrorsException("Setting " + key + " must be a number, not \"" + value + "\".");
            }
            return result;
        }
    }
}
