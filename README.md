# MySQL Manager

Admin platform for MySQL backups, copy, clone, and restore. Source databases stay read-only. The API records a job and returns immediately. A worker runs the dump, encryption, Git sync, and import.

## Layout

- `src/Backend/MySqlManager.Domain` — entities and safety rules
- `src/Backend/MySqlManager.Application` — MediatR handlers and ports
- `src/Backend/MySqlManager.Infrastructure` — EF Core, MySQL client, storage, Git, pipelines
- `src/Backend/MySqlManager.Api` — HTTP, JWT, SignalR
- `src/Backend/MySqlManager.Worker` — backup, copy, and restore workers
- `src/Frontend/mysql-manager-web` — Vue 3 admin UI
- `tests` — domain, infrastructure, and API tests

## Install

Requires the .NET 10 SDK. The worker needs `mysqldump`, `mysql`, and `git` available at the paths in configuration. Node 22 builds the UI.

```bash
dotnet build MySqlManager.slnx
dotnet test MySqlManager.slnx
cd src/Frontend/mysql-manager-web
npm install
npm run dev
```

The API listens on `http://localhost:5220`. The Vite dev server proxies `/api` and `/hubs` to that port.

## Configuration

Copy `src/Backend/MySqlManager.Api/appsettings.example.json`. Real passwords stay out of source control. `appsettings.Development.json` contains a documented development administrator and a development-only encryption key. Those values are not production secrets.

Sections: management connection string, JWT, backup root and temp paths, retention, encryption key, Git (`AllowHistoryRewrite` defaults to false), job concurrency, storage, secret provider, notifications, and CORS.

## Management database

MySQL 8. On startup the API applies EF Core migrations to the management database only. It never migrates a customer server. Development seed creates SuperAdmin, Administrator, Operator, and Viewer, plus an administrator from `Seed:AdminPassword` when that value is set and no users exist.

## Backup

`POST /api/backups` inserts a pending job and returns `BACKUP_JOB_CREATED` with `jobId`. The worker leases the job, checks free space, streams `mysqldump` through gzip and chunked AES-256-GCM, checksums the ciphertext, verifies it, then promotes the file. Paths look like `{root}/{serverId}/{database}/daily/{database}_{timestamp}.sql.gz.enc`.

Retention keeps seven backups that are both completed and verified. A failed eighth day does not delete the oldest success. Failed, running, and unverified files do not count.

Create a dedicated read-only account on each source server before registering it. The SQL and the privileges the connection test accepts are in [docs/deployment.md](docs/deployment.md#backup-user). Save that account in the admin UI as **Backup read-only**. Destination imports use a separate credential.

## Git

One repository per server. Modes are metadata-only, backup files with Git LFS, and rolling repository. The working tree keeps the latest seven files. History rewrite runs only when `AllowGitHistoryRewrite` is true and an administrator types `PURGE`.

## Copy and restore

Copy and clone read the source and create a new destination database. Restore imports a verified backup into a new database name. If that name already exists, the job stops. There is no move mode, no source cleanup, and no drop of an existing destination.

## Security

Source credentials are rejected when `SHOW GRANTS` includes write privileges. Destination import uses a separate destination credential. Ad-hoc SQL allows only read statements. API responses never include passwords. Audit records for backup and copy say source modification is none.

## Production

See [docs/deployment.md](docs/deployment.md) for Ubuntu, Apache, systemd, split hosts, and Docker. Generate a new JWT signing key and a 32-byte `Backup:MasterKeyBase64` before the first real backup.
