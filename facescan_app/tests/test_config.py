from urllib.parse import unquote_plus

import pytest

from facescan_app import sqlserver
from facescan_app.config import load_config

CONN = ("Data Source=192.168.1.10;Initial Catalog=cm_demo; Persist Security Info=false; "
        "User ID=someuser;Password=secret")


def test_dotnet_connection_string_to_odbc():
    assert sqlserver.to_odbc(CONN) == (
        "DRIVER={ODBC Driver 18 for SQL Server};SERVER=192.168.1.10;DATABASE=cm_demo;"
        "UID=someuser;PWD=secret;TrustServerCertificate=yes"
    )


def test_database_override_replaces_initial_catalog():
    assert "DATABASE=cm_clinic2;" in sqlserver.to_odbc(CONN, database="cm_clinic2")
    assert sqlserver.database_name(CONN) == "cm_demo"
    assert sqlserver.database_name(CONN, "cm_clinic2") == "cm_clinic2"


def test_special_characters_in_password():
    odbc = sqlserver.to_odbc('Server=srv;Database=db;User ID=u;Password="p;w}d"')
    assert "PWD={p;w}}d}" in odbc


def test_missing_database_is_an_error():
    with pytest.raises(ValueError):
        sqlserver.to_odbc("Data Source=srv;User ID=u;Password=p")


def test_load_config_per_clinic(tmp_path):
    ini = tmp_path / "config.ini"
    ini.write_text(
        "[database]\n"
        "connection_string = Data Source=192.168.1.10;User ID=someuser;Password=50%off\n"
        "database = cm_demo\n"
    )
    config = load_config(str(ini))
    assert config.database == "cm_demo"
    assert config.store_path == "faces_cm_demo.db"
    assert config.db_url.startswith("mssql+pyodbc:///?odbc_connect=")
    assert "DATABASE=cm_demo;" in unquote_plus(config.db_url)
    assert "PWD=50%off;" in unquote_plus(config.db_url)

    other = load_config(str(ini), database="cm_clinic2")
    assert other.store_path == "faces_cm_clinic2.db"
    assert "DATABASE=cm_clinic2;" in unquote_plus(other.db_url)
