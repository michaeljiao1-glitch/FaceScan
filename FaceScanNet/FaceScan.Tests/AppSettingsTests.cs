using System.Collections.Specialized;
using System.Configuration;
using System.Data.SqlClient;
using NUnit.Framework;

namespace FaceScan.Tests
{
    [TestFixture]
    public class AppSettingsTests
    {
        private const string Connection =
            "Data Source=192.168.1.10;Initial Catalog=cm_demo; Persist Security Info=false; User ID=someuser;Password=secret";

        [Test]
        public void UsesInitialCatalogFromConnectionString()
        {
            var settings = AppSettings.FromValues(Connection, new NameValueCollection(), new string[0], @"C:\FaceScan");
            Assert.AreEqual("cm_demo", settings.DatabaseName);
            var builder = new SqlConnectionStringBuilder(settings.ConnectionString);
            Assert.AreEqual("192.168.1.10", builder.DataSource);
            Assert.AreEqual("someuser", builder.UserID);
            Assert.AreEqual("secret", builder.Password);
        }

        [TestCase("/database:cm_clinic2")]
        [TestCase("-database=cm_clinic2")]
        [TestCase("--database", "cm_clinic2")]
        public void CommandLineOverridesDatabase(params string[] args)
        {
            var values = new NameValueCollection { { "Database", "cm_fromconfig" } };
            var settings = AppSettings.FromValues(Connection, values, args, @"C:\FaceScan");
            Assert.AreEqual("cm_clinic2", settings.DatabaseName);
            Assert.AreEqual("cm_clinic2", new SqlConnectionStringBuilder(settings.ConnectionString).InitialCatalog);
        }

        [Test]
        public void ConfigSettingOverridesConnectionString()
        {
            var values = new NameValueCollection { { "Database", "cm_fromconfig" } };
            Assert.AreEqual("cm_fromconfig", AppSettings.FromValues(Connection, values, null, @"C:\FaceScan").DatabaseName);
        }

        [Test]
        public void EachClinicGetsItsOwnFaceFile()
        {
            var values = new NameValueCollection { { "FaceDataFolder", @"D:\Faces" } };
            var a = AppSettings.FromValues(Connection, values, null, @"C:\FaceScan");
            var b = AppSettings.FromValues(Connection, values, new[] { "/database:cm_clinic2" }, @"C:\FaceScan");
            StringAssert.EndsWith("faces_cm_demo.dat", a.FaceStorePath);
            StringAssert.EndsWith("faces_cm_clinic2.dat", b.FaceStorePath);
        }

        [Test]
        public void DefaultsAndNumbers()
        {
            var values = new NameValueCollection { { "MatchThreshold", "0.45" }, { "MinMinutesBeforeCheckout", "10" } };
            var settings = AppSettings.FromValues(Connection, values, null, @"C:\FaceScan");
            Assert.AreEqual(0.45, settings.MatchThreshold, 1e-9);
            Assert.AreEqual(10, settings.MinTimeBeforeCheckout.TotalMinutes, 1e-9);
            Assert.AreEqual("people", settings.PeopleTable);
            Assert.AreEqual("appointments", settings.AppointmentsTable);
            Assert.AreEqual("dbo", settings.Schema);
        }

        [Test]
        public void MissingDatabaseIsAnError()
        {
            Assert.Throws<ConfigurationErrorsException>(() => AppSettings.FromValues(
                "Data Source=srv;User ID=u;Password=p", new NameValueCollection(), null, @"C:\FaceScan"));
        }
    }
}
