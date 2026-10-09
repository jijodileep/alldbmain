# Deployment

## Ubuntu hosts

Install the .NET 10 ASP.NET runtime on the API and worker hosts, and Node only where you build the Vue app. The worker host also needs `default-mysql-client` and `git` with Git LFS. `mysqldump` and `mysql` are not assumed to be on the developer workstation PATH; set `Backup:MySqlDumpPath` and `Backup:MySqlClientPath`.

```bash
sudo apt-get update
sudo apt-get install -y default-mysql-client git git-lfs
git lfs install --system
```

## Split hosts

The API, worker, management MySQL, and backup disk can run on different machines.

- API host: serves HTTP and SignalR. It does not run `mysqldump`.
- Worker host: holds the backup root, temp directory, and Git workspace. It reaches source MySQL on port 3306 read-only and destination MySQL with a separate destination credential.
- Database host: management MySQL 8 only. EF Core migrations run only against this database.
- Storage: local disk, or S3 / Azure Blob when `Storage:Type` is set. The worker still writes a local temp stream before upload.

Point both API and worker at the same management connection string. Mount the same backup volume on the worker. Do not mount it as required storage for the API.

## Backup user

Create this account on each source server, signed in as an administrator. The connection test accepts it only when it can read data and cannot change it. Use a different account for destination imports.

```sql
CREATE USER 'backup'@'%' IDENTIFIED BY 'a-long-random-password';

GRANT SELECT, SHOW VIEW, TRIGGER, EVENT, LOCK TABLES ON your_database.* TO 'backup'@'%';
GRANT PROCESS ON *.* TO 'backup'@'%';
```

On MySQL 8, also grant this so `mysqldump` can include stored routines:

```sql
GRANT SHOW_ROUTINE ON *.* TO 'backup'@'%';
```

`%` lets the worker connect from another machine. If the worker runs on the same computer as the database, use `'backup'@'localhost'` instead. Replace `your_database` with the schema to back up, and repeat that `GRANT` for each schema this account should read.

Do not grant `INSERT`, `UPDATE`, `DELETE`, `CREATE`, `ALTER`, `DROP`, `TRUNCATE`, `INDEX`, `ALL PRIVILEGES`, or `GRANT OPTION`. The connection test rejects those.

In the admin UI, open the server and save a credential with purpose **Backup read-only**, username `backup`, and that password.

## Encryption key

Generate a 32-byte key and store only the base64 form in the secret store or environment, never in Git:

```bash
openssl rand -base64 32
```

Set `Backup:MasterKeyBase64`. Losing this key makes existing backup files unreadable. Rotate through Settings in the admin UI; rotation rewraps the data key and does not rewrite backup files.

## Apache

The admin site is served by Apache. The API stays on `127.0.0.1:8080`. Apache proxies `/api` and `/hubs`, and serves the Vue build from `/var/www/mysql-manager`. The `/hubs` proxy must allow WebSocket upgrade so job progress can connect.

Enable the modules, copy the built frontend, and enable the site in `deploy/apache/mysql-manager.conf`. Change `ServerName` before enabling it. Terminate TLS on Apache.

```bash
sudo a2enmod proxy proxy_http proxy_wstunnel headers rewrite
sudo mkdir -p /var/www/mysql-manager
sudo cp -a src/Frontend/mysql-manager-web/dist/. /var/www/mysql-manager/
sudo cp deploy/apache/mysql-manager.conf /etc/apache2/sites-available/mysql-manager.conf
sudo a2ensite mysql-manager
sudo systemctl reload apache2
```

## systemd

Sample units are in `deploy/systemd`. Copy the published API to `/opt/mysql-manager/api` and the worker to `/opt/mysql-manager/worker`. Put production settings in environment variables or a root-only `appsettings.Production.json`.

```bash
sudo systemctl enable --now mysql-manager-api mysql-manager-worker
```

## Docker

`docker-compose.yml` starts management MySQL, the API, the worker (with the MySQL client and Git LFS), and the frontend. Redis is optional (`docker compose --profile redis up`). Compose uses the documented development passwords. Replace them before any shared environment.

The worker image installs `default-mysql-client` and `git-lfs`. Local Windows development can leave those paths configured to a client install instead of relying on PATH.
