using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;

namespace FaceScan
{
    public enum ScanOutcome
    {
        CheckedIn,
        CheckedOut,
        AlreadyCheckedIn,
        VisitComplete,
        NoAppointment,
    }

    public sealed class ScanResult
    {
        public ScanResult(ScanOutcome outcome, DateTime? time)
        {
            Outcome = outcome;
            Time = time;
        }

        public ScanOutcome Outcome { get; private set; }

        /// <summary>Arrival or departure time that applies to the outcome, if any.</summary>
        public DateTime? Time { get; private set; }
    }

    /// <summary>Looks up patients and today's appointments, and records arrival and departure times.</summary>
    public sealed class ClinicDatabase
    {
        private static readonly string[] NumericTypes =
            { "int", "bigint", "smallint", "tinyint", "decimal", "numeric" };

        private readonly string connectionString;
        private readonly string schema;
        private readonly string people;
        private readonly string appointments;
        private readonly string peopleName;
        private readonly string appointmentsName;
        private readonly TimeSpan minTimeBeforeCheckout;
        private string peopleAccountType;
        private string appointmentsAccountType;

        public ClinicDatabase(string connectionString, string schema, string peopleTable,
            string appointmentsTable, TimeSpan minTimeBeforeCheckout)
        {
            this.connectionString = connectionString;
            this.schema = schema;
            peopleName = peopleTable;
            appointmentsName = appointmentsTable;
            people = Quote(schema) + "." + Quote(peopleTable);
            appointments = Quote(schema) + "." + Quote(appointmentsTable);
            this.minTimeBeforeCheckout = minTimeBeforeCheckout;
        }

        /// <summary>Connects and checks the tables and columns FaceScan needs are there.</summary>
        public void Initialize()
        {
            using (var conn = Open())
            {
                var peopleColumns = Columns(conn, peopleName);
                var appointmentColumns = Columns(conn, appointmentsName);
                Require(peopleName, peopleColumns, "account");
                Require(appointmentsName, appointmentColumns,
                    "account", "appt_date", "arrival_datetime", "end_datetime");
                peopleAccountType = peopleColumns["account"];
                appointmentsAccountType = appointmentColumns["account"];
            }
        }

        public bool AccountExists(string account)
        {
            object value;
            if (!TryAccountValue(account, peopleAccountType, out value))
            {
                return false;
            }
            using (var conn = Open())
            using (var cmd = new SqlCommand("SELECT TOP (1) 1 FROM " + people + " WHERE account = @account", conn))
            {
                cmd.Parameters.Add(AccountParameter(value, peopleAccountType));
                return cmd.ExecuteScalar() != null;
            }
        }

        /// <summary>
        /// Applies a face scan to the patient's appointments today: a visit in progress (arrived, not
        /// ended) gets end_datetime set; otherwise the visit not yet arrived that is scheduled closest
        /// to now gets arrival_datetime set.
        /// </summary>
        public ScanResult RecordScan(string account, DateTime now)
        {
            now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, now.Kind);
            object value;
            if (!TryAccountValue(account, appointmentsAccountType, out value))
            {
                return new ScanResult(ScanOutcome.NoAppointment, null);
            }

            using (var conn = Open())
            using (var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted))
            {
                // UPDLOCK keeps a second kiosk from changing these rows until this scan is recorded
                var visits = new List<Visit>();
                using (var cmd = Command(conn, tx,
                    "SELECT appt_date, arrival_datetime, end_datetime FROM " + appointments +
                    " WITH (UPDLOCK) WHERE " + TodayFilter, value, now))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        visits.Add(new Visit
                        {
                            ApptDate = reader.GetDateTime(0),
                            Arrival = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1),
                            End = reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2),
                        });
                    }
                }

                var result = Decide(visits, now);
                if (result.Outcome == ScanOutcome.CheckedOut)
                {
                    // the same visit Decide picked: most recent arrival without an end time
                    Execute(conn, tx,
                        ";WITH visit AS (SELECT TOP (1) * FROM " + appointments + " WHERE " + TodayFilter +
                        " AND arrival_datetime IS NOT NULL AND end_datetime IS NULL" +
                        " ORDER BY arrival_datetime DESC) UPDATE visit SET end_datetime = @now",
                        value, now);
                }
                else if (result.Outcome == ScanOutcome.CheckedIn)
                {
                    // the same visit Decide picked: not yet arrived, scheduled closest to now
                    Execute(conn, tx,
                        ";WITH visit AS (SELECT TOP (1) * FROM " + appointments + " WHERE " + TodayFilter +
                        " AND arrival_datetime IS NULL" +
                        " ORDER BY ABS(DATEDIFF(SECOND, appt_date, @now)), appt_date)" +
                        " UPDATE visit SET arrival_datetime = @now",
                        value, now);
                }
                tx.Commit();
                return result;
            }
        }

        private const string TodayFilter = "account = @account AND appt_date >= @dayStart AND appt_date < @dayEnd";

        internal sealed class Visit
        {
            public DateTime ApptDate;
            public DateTime? Arrival;
            public DateTime? End;
        }

        internal ScanResult Decide(IList<Visit> visits, DateTime now)
        {
            if (visits.Count == 0)
            {
                return new ScanResult(ScanOutcome.NoAppointment, null);
            }

            var inProgress = visits.Where(v => v.Arrival.HasValue && !v.End.HasValue)
                .OrderByDescending(v => v.Arrival.Value).FirstOrDefault();
            if (inProgress != null)
            {
                if (now - inProgress.Arrival.Value < minTimeBeforeCheckout)
                {
                    return new ScanResult(ScanOutcome.AlreadyCheckedIn, inProgress.Arrival);
                }
                return new ScanResult(ScanOutcome.CheckedOut, now);
            }

            if (visits.Any(v => !v.Arrival.HasValue))
            {
                return new ScanResult(ScanOutcome.CheckedIn, now);
            }
            return new ScanResult(ScanOutcome.VisitComplete, null);
        }

        private SqlConnection Open()
        {
            var conn = new SqlConnection(connectionString);
            conn.Open();
            return conn;
        }

        private SqlCommand Command(SqlConnection conn, SqlTransaction tx, string sql, object account, DateTime now)
        {
            var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.Add(AccountParameter(account, appointmentsAccountType));
            cmd.Parameters.Add("@dayStart", SqlDbType.DateTime).Value = now.Date;
            cmd.Parameters.Add("@dayEnd", SqlDbType.DateTime).Value = now.Date.AddDays(1);
            cmd.Parameters.Add("@now", SqlDbType.DateTime).Value = now;
            return cmd;
        }

        private void Execute(SqlConnection conn, SqlTransaction tx, string sql, object account, DateTime now)
        {
            using (var cmd = Command(conn, tx, sql, account, now))
            {
                cmd.ExecuteNonQuery();
            }
        }

        private Dictionary<string, string> Columns(SqlConnection conn, string table)
        {
            var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = new SqlCommand(
                "SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table", conn))
            {
                cmd.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = schema;
                cmd.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = table;
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        columns[reader.GetString(0)] = reader.GetString(1).ToLowerInvariant();
                    }
                }
            }
            if (columns.Count == 0)
            {
                throw new InvalidOperationException(string.Format(
                    "Table {0}.{1} was not found in database {2}, or this login cannot read it.",
                    schema, table, conn.Database));
            }
            return columns;
        }

        private static void Require(string table, Dictionary<string, string> columns, params string[] names)
        {
            var missing = names.Where(n => !columns.ContainsKey(n)).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    "Table " + table + " is missing column(s): " + string.Join(", ", missing));
            }
        }

        private static bool TryAccountValue(string account, string sqlType, out object value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(account))
            {
                return false;
            }
            account = account.Trim();
            if (NumericTypes.Contains(sqlType))
            {
                long number;
                if (!long.TryParse(account, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                {
                    return false;
                }
                value = number;
                return true;
            }
            value = account;
            return true;
        }

        private static SqlParameter AccountParameter(object value, string sqlType)
        {
            if (value is long)
            {
                return new SqlParameter("@account", SqlDbType.BigInt) { Value = value };
            }
            // match the column's string type so SQL Server can use its index
            var type = sqlType == "varchar" || sqlType == "char" ? SqlDbType.VarChar : SqlDbType.NVarChar;
            return new SqlParameter("@account", type, 100) { Value = value };
        }

        private static string Quote(string identifier)
        {
            return "[" + identifier.Replace("]", "]]") + "]";
        }
    }
}
