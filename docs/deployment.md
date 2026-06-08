# Deployment

The supported deployment is the checked-in Docker Compose stack with PostgreSQL plus the two .NET services. ILD and the WorkItem Server both run EF Core migrations against PostgreSQL when connection strings are configured.

## Docker Compose

```bash
git clone <this repo> ild && cd ild
cp .env.example .env
# set ILD_PASSWORD before continuing
docker compose up --build
```

The compose stack starts three services:

- `postgres` on port `5432`
- `workitem-server` on port `8081`
- `ild` on port `8080`

Open <http://localhost:8080> and log in with the configured username (`admin` by default, or `ILD_USERNAME`) and the `ILD_PASSWORD` value you supplied.

## Volumes

| Volume          | Purpose                                                |
| --------------- | ------------------------------------------------------ |
| `postgres-data` | PostgreSQL data for both ILD and the WorkItem Server   |
| `ild-data`      | ILD runtime files under `/data`                        |
| `ild-worktrees` | Per-work-item git worktrees                            |
| `workitem-data` | Additional WorkItem Server runtime files under `/data` |

Your host `~/.gitconfig` is mounted read-only into the ILD container so commits inherit your local name and email unless you override `GIT_CONFIG`.

For host bind mounts instead of named volumes:

```yaml
volumes:
  - ./.local/ild-data:/data
  - ./.local/ild-worktrees:/worktrees
  - ./.local/workitem-data:/data
```

## Images

The main `Dockerfile` builds the frontend, publishes the .NET host, and optionally installs additional runtime tooling used by work-item execution (see [Configuration](./configuration.md#build-time-container-options)). `Dockerfile.WorkItemServer` builds the separate WorkItem Server image.

## First-startup behavior

On first successful ILD startup:

1. EF Core migrations are applied when a database connection string is configured.
2. The bootstrap user is created on first login from `ILD_USERNAME` (default `admin`) and `ILD_PASSWORD`.
3. Seed loop templates are created: `Simple Code Change`, `AI-Assisted Feature`, and `Plan`.
4. The global WorkItem Server connection is auto-seeded when `ILD_WORKITEM_SERVER_URL` and `ILD_WORKITEM_SERVER_API_KEY` are present and no URL is configured yet.
5. Recoverable runs are inspected and recovery is attempted according to each run's policy.
