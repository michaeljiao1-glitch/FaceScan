using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using NUnit.Framework;

namespace FaceScan.Tests
{
    /// <summary>
    /// Runs against a real SQL Server. Set the environment variable FACESCAN_TEST_SQL to a connection
    /// string for a login that may create databases (e.g. a local SQL Server Express); each test
    /// creates and drops its own scratch database. Skipped when the variable is not set.
    /// </summary>
    [TestFixture]
    public class ClinicDatabaseTests
    {
        private static readonly DateTime Today = DateTime.Today;
        private static readonly DateTime Now = Today.AddHours(9).AddMinutes(5);

        private string server;
        private string databaseName;
        private string connectionString;

        [SetUp]
        public void SetUp()
        {
            server = Environment.GetEnvironmentVariable("FACESCAN_TEST_SQL");
            if (string.IsNullOrEmpty(server))
            {
                Assert.Ignore("FACESCAN_TEST_SQL is not set.");
            }
            databaseName = "facescan_test_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Run(server, "CREATE DATABASE " + databaseName);
            connectionString = AppSettings.WithDatabase(server, databaseName);
        }

        [TearDown]
        public void TearDown()
        {
            if (databaseName != null)
            {
                SqlConnection.ClearAllPools();
                Run(server, "ALTER DATABASE " + databaseName + " SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE " + databaseName);
            }
        }

        private static void Run(string connection, string sql, params object[] args)
        {
            using (var conn = new SqlConnection(connection))
            using (var cmd = new SqlCommand(sql, conn))
            {
                for (var i = 0; i < args.Length; i++)
                {
                    cmd.Parameters.AddWithValue("@p" + i, args[i]);
                }
                conn.Open();
                cmd.ExecuteNonQuery();
            }
        }

        private ClinicDatabase CreateTables(string accountType, string apptType, string idColumn = "")
        {
            Run(connectionString,
                "CREATE TABLE people (account " + accountType + " NOT NULL);" +
                "CREATE TABLE appointments (" + idColumn + "account " + accountType + ", appt_date " + apptType +
                ", arrival_datetime DATETIME, end_datetime DATETIME);" +
                "INSERT INTO people VALUES (1001), (1002);");
            var db = new ClinicDatabase(connectionString, "dbo", "people", "appointments", TimeSpan.FromMinutes(5));
            db.Initialize();
            return db;
        }

        private void AddAppointment(object account, DateTime apptDate)
        {
            Run(connectionString, "INSERT INTO appointments (account, appt_date) VALUES (@p0, @p1)", account, apptDate);
        }

        private List<object[]> Visits()
        {
            var rows = new List<object[]>();
            using (var conn = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(
                "SELECT account, appt_date, arrival_datetime, end_datetime FROM appointments ORDER BY account, appt_date", conn))
            {
                conn.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var row = new object[4];
                        reader.GetValues(row);
                        rows.Add(row);
                    }
                }
            }
            return rows;
        }

        [TestCase("INT", "DATETIME", "")]
        [TestCase("INT", "DATE", "")]
        [TestCase("VARCHAR(20)", "DATETIME2(0)", "id INT IDENTITY PRIMARY KEY, ")]
        public void ArrivalThenDeparture(string accountType, string apptType, string idColumn)
        {
            var db = CreateTables(accountType, apptType, idColumn);
            AddAppointment(1001, Today.AddHours(9));

            var arrival = db.RecordScan("1001", Now);
            Assert.AreEqual(ScanOutcome.CheckedIn, arrival.Outcome);
            Assert.AreEqual(Now, Visits()[0][2]);
            Assert.AreEqual(DBNull.Value, Visits()[0][3]);

            // an immediate second scan is a double scan, not a checkout
            Assert.AreEqual(ScanOutcome.AlreadyCheckedIn, db.RecordScan("1001", Now.AddMinutes(1)).Outcome);

            var leave = Now.AddMinutes(45);
            Assert.AreEqual(ScanOutcome.CheckedOut, db.RecordScan("1001", leave).Outcome);
            Assert.AreEqual(Now, Visits()[0][2]);
            Assert.AreEqual(leave, Visits()[0][3]);

            Assert.AreEqual(ScanOutcome.VisitComplete, db.RecordScan("1001", leave.AddMinutes(10)).Outcome);
        }

        [Test]
        public void NoAppointmentToday()
        {
            var db = CreateTables("INT", "DATETIME");
            AddAppointment(1001, Today.AddDays(1).AddHours(9));
            AddAppointment(1002, Today.AddHours(10));
            Assert.AreEqual(ScanOutcome.NoAppointment, db.RecordScan("1001", Now).Outcome);
            Assert.AreEqual(ScanOutcome.NoAppointment, db.RecordScan("9999", Now).Outcome);
            Assert.AreEqual(ScanOutcome.NoAppointment, db.RecordScan("abc", Now).Outcome);
            Visits().ForEach(v => Assert.AreEqual(DBNull.Value, v[2]));
        }

        [Test]
        public void ChecksInToTheAppointmentClosestToNow()
        {
            var db = CreateTables("INT", "DATETIME");
            AddAppointment(1001, Today.AddHours(8));
            AddAppointment(1001, Today.AddHours(15));
            db.RecordScan("1001", Today.AddHours(14).AddMinutes(40));
            var visits = Visits();
            Assert.AreEqual(DBNull.Value, visits[0][2]);
            Assert.AreNotEqual(DBNull.Value, visits[1][2]);
        }

        [Test]
        public void OnlyTheScannedPatientChanges()
        {
            var db = CreateTables("INT", "DATETIME");
            AddAppointment(1001, Today.AddHours(9));
            AddAppointment(1002, Today.AddHours(9));
            db.RecordScan("1001", Now);
            var visits = Visits();
            Assert.AreNotEqual(DBNull.Value, visits[0][2]);
            Assert.AreEqual(DBNull.Value, visits[1][2]);
        }

        [Test]
        public void AccountExists()
        {
            var db = CreateTables("INT", "DATETIME");
            Assert.IsTrue(db.AccountExists("1001"));
            Assert.IsTrue(db.AccountExists(" 1002 "));
            Assert.IsFalse(db.AccountExists("9999"));
            Assert.IsFalse(db.AccountExists("abc"));
        }

        [Test]
        public void MissingColumnIsReported()
        {
            Run(connectionString, "CREATE TABLE people (account INT); CREATE TABLE appointments (account INT, appt_date DATE)");
            var db = new ClinicDatabase(connectionString, "dbo", "people", "appointments", TimeSpan.FromMinutes(5));
            var error = Assert.Throws<InvalidOperationException>(db.Initialize);
            StringAssert.Contains("arrival_datetime", error.Message);
        }
    }
}
