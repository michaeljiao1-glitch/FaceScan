"""Turn a .NET-style SQL Server connection string into a SQLAlchemy URL (via pyodbc)."""

from typing import Dict, Optional
from urllib.parse import quote_plus

DEFAULT_DRIVER = "ODBC Driver 18 for SQL Server"

# .NET keyword -> ODBC keyword
_KEYWORDS = {
    "data source": "SERVER",
    "server": "SERVER",
    "address": "SERVER",
    "addr": "SERVER",
    "network address": "SERVER",
    "initial catalog": "DATABASE",
    "database": "DATABASE",
    "user id": "UID",
    "uid": "UID",
    "user": "UID",
    "password": "PWD",
    "pwd": "PWD",
    "encrypt": "Encrypt",
    "trustservercertificate": "TrustServerCertificate",
    "trust server certificate": "TrustServerCertificate",
    "connect timeout": "Connection Timeout",
    "connection timeout": "Connection Timeout",
    "application name": "APP",
}

# keywords that only matter to .NET and have no ODBC equivalent
_IGNORED = {"persist security info", "multipleactiveresultsets", "pooling", "max pool size",
            "min pool size", "packet size"}


def parse_connection_string(connection_string: str) -> Dict[str, str]:
    """Parse 'Key=Value; Key2="quoted;value"' into a dict keyed by lower-case keyword."""
    result: Dict[str, str] = {}
    i, text = 0, connection_string
    while i < len(text):
        eq = text.find("=", i)
        if eq == -1:
            break
        key = text[i:eq].strip().strip(";").strip().lower()
        i = eq + 1
        while i < len(text) and text[i] == " ":
            i += 1
        if i < len(text) and text[i] in "\"'":
            quote = text[i]
            end = text.find(quote, i + 1)
            if end == -1:
                raise ValueError("Unclosed quote in connection string")
            value = text[i + 1:end]
            semi = text.find(";", end)
            i = len(text) if semi == -1 else semi + 1
        else:
            semi = text.find(";", i)
            end = len(text) if semi == -1 else semi
            value = text[i:end].strip()
            i = end + 1
        if key:
            result[key] = value
    return result


def _odbc_value(value: str) -> str:
    if any(ch in value for ch in ";{}") or value != value.strip():
        return "{" + value.replace("}", "}}") + "}"
    return value


def to_odbc(connection_string: str, database: Optional[str] = None,
            driver: str = DEFAULT_DRIVER) -> str:
    """ODBC connection string; `database` (if given) replaces Initial Catalog."""
    odbc: Dict[str, str] = {"DRIVER": "{" + driver + "}"}
    for key, value in parse_connection_string(connection_string).items():
        if key in _IGNORED:
            continue
        if key in ("integrated security", "trusted_connection"):
            if value.lower() in ("true", "yes", "sspi"):
                odbc["Trusted_Connection"] = "yes"
            continue
        if key not in _KEYWORDS:
            raise ValueError(f"Unsupported connection string keyword: {key!r}")
        if _KEYWORDS[key] in ("Encrypt", "TrustServerCertificate"):
            value = "yes" if value.lower() in ("true", "yes", "mandatory", "strict") else "no"
        odbc[_KEYWORDS[key]] = value
    if database:
        odbc["DATABASE"] = database
    if "SERVER" not in odbc:
        raise ValueError("Connection string has no Data Source / Server")
    if "DATABASE" not in odbc:
        raise ValueError("No database: set Initial Catalog or [database] database in config.ini")
    # ODBC Driver 18 encrypts by default and rejects the self-signed certificate most
    # on-premises SQL Servers use; trust it unless the connection string says otherwise.
    odbc.setdefault("TrustServerCertificate", "yes")
    return ";".join(f"{k}={_odbc_value(v) if k != 'DRIVER' else v}" for k, v in odbc.items())


def database_name(connection_string: str, database: Optional[str] = None) -> Optional[str]:
    if database:
        return database
    parsed = parse_connection_string(connection_string)
    return parsed.get("initial catalog") or parsed.get("database")


def to_sqlalchemy_url(connection_string: str, database: Optional[str] = None,
                      driver: str = DEFAULT_DRIVER) -> str:
    return "mssql+pyodbc:///?odbc_connect=" + quote_plus(to_odbc(connection_string, database, driver))
