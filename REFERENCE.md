# DtPipe CLI Reference

> **Docs map:** [README.md](./README.md) — quick start · [COOKBOOK.md](./COOKBOOK.md) — recipes · [EXTENDING.md](./EXTENDING.md) — new adapters/transformers · [CLAUDE.md](./CLAUDE.md) — contributor internals.

## Table of Contents

- [Synopsis](#synopsis)
- [Core Options](#core-options)
- [Secret Management](#secret-management)
- [Value Resolution](#value-resolution)
- [Providers](#providers)
- [Source (Reader) Options](#source-reader-options)
- [Data Transformations](#data-transformations)
- [Schema & Projection](#schema--projection)
- [Target (Writer) Options](#target-writer-options)
- [Execution & Statistics](#execution--statistics)
- [DAG Syntax](#dag-syntax)
- [YAML Job File Schema](#yaml-job-file-schema)
- [Provider-Specific Options](#provider-specific-options)
- [Incremental Loading](#incremental-loading)
- [Shell Completion](#shell-completion)
- [Model Context Protocol (MCP) Server](#model-context-protocol-mcp-server)
- [AI Agent Subcommand](#ai-agent-subcommand-dtpipe-agent)
- [Agent Guardrails](#agent-guardrails-isqlsafetypolicy--iapprovalgate)

## Synopsis

```
dtpipe -i SOURCE [OPTIONS] -o DESTINATION
dtpipe --job FILE [OVERRIDES]
```

---

## Core Options

| Flag | Description |
|:---|:---|
| `-i`, `--input SOURCE` | Source ADO.NET connection string or file path |
| `-o`, `--output DEST` | Target ADO.NET connection string or file path |
| `-q`, `--query SQL` | SQL query (required for database sources) |
| `--dry-run [N]` | Preview N rows without writing (default: 10) |
| `--retry` | Enable Polly database retry policy with exponential backoff & jitter |
| `--job FILE`, `-j FILE` | Load a pipeline from a YAML job file |
| `--export-job FILE` | Serialize the current CLI pipeline to YAML and exit |
| `--alias NAME` | Name the current branch for DAG references |
| `--cursor COLUMN` | Cursor column for incremental loading (writer-side tracking) |
| `--state PATH` | State file path for cursor persistence (writer-side) |
| `--cursor-from VALUE` | Global override cursor value for this run |
| `--version` | Print version and exit |

---

## Secret Management

DtPipe stores secrets in the OS credential store (macOS Keychain, Windows Credential Manager, Linux Secret Service).

```bash
# Store a connection string
dtpipe secret set prod-db "pg:Host=...;Password=secret"

# Use it as a connection string
dtpipe -i keyring://prod-db --query "SELECT * FROM users" -o users.parquet

# Inline substitution within a larger string
dtpipe -i duck:memory --duck-init "LOAD httpfs; SET s3_access_key_id='${{keyring://aws-key}}';" ...
```

| Command | Description |
|:---|:---|
| `dtpipe secret set <alias> <value>` | Store or update a secret |
| `dtpipe secret list` | List all stored aliases |
| `dtpipe secret get <alias>` | Print a secret value |
| `dtpipe secret delete <alias>` | Delete a specific secret |
| `dtpipe secret nuke` | Delete all stored secrets |

Secrets can be referenced in two ways:
- **`keyring://alias`** — replaces the entire value (connection strings, `--duck-init`)
- **`${{keyring://alias}}`** — inline substitution within a string

> See [Value Resolution](#value-resolution) for the full resolution pipeline, supported contexts, and CLI/YAML differences.

---

## Value Resolution

DtPipe resolves string values through a sequential pipeline before use. The available mechanisms depend on the context.

### Resolution pipeline

1. **Full-value replacement** (mutually exclusive — first match wins):
   - `@/path/to/file` — load entire file content
   - `keyring://alias` — load full value from OS keyring

2. **Inline substitution** (applied to the result of step 1):
   - `${{ENV_VAR}}` — substitute an environment variable
   - `${{keyring://alias}}` — substitute an inline keyring secret
   - `${{cursor://path|default}}` — substitute a cursor value from a state file (with optional default value if the state file does not exist)

Steps are composable: a keyring block can itself contain `${{ENV_VAR}}` placeholders that are resolved afterwards.

### Compatibility matrix

Not all mechanisms are available in every context:

| Context | `@file` | `keyring://` | `${{ENV_VAR}}` | `${{keyring://…}}` | `${{cursor://…}}` |
|:---|:---:|:---:|:---:|:---:|:---:|
| Connection strings (`-i`, `-o`) | ✅ | ✅ | ✅ | ✅ | ✅ |
| `--duck-init` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `--query` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `--pre-exec`, `--post-exec`, etc. | ✅ | ✅ | ✅ | ✅ | ✅ |
| `--compute`, `--expand`, `--filter` scripts| ✅ | ✅ | ✅ | ✅ | ✅ |
| YAML job files (all values) | — | — | ✅ | ✅ | ✅ |

> [!IMPORTANT]
> **YAML Interpolation**: In YAML job files, `${{ENV_VAR}}`, `${{keyring://...}}` and
> `${{cursor://...}}` interpolations run through the same resolver engine as the CLI
> (env → keyring → cursor, in that order) and are applied to the raw YAML text *before*
> parsing — so they work on **all** values (including configuration properties that aren't
> normally resolved, like `batch-size` or `separator`). Unresolved variables are left
> verbatim in the text.
> Full-value replacement (`@file` and `keyring://alias` without braces) only works for specific string fields that pass through the CLI resolver (connection strings, queries, hooks, and transformer scripts).

### Examples

```bash
# Connection string from keyring (full replacement)
dtpipe -i keyring://prod-db -q "SELECT * FROM users" -o users.parquet

# Inline keyring secrets in duck-init
dtpipe -i duck:memory \
  --duck-init "LOAD httpfs; SET s3_access_key_id='${{keyring://aws-key}}';" \
  -q "SELECT * FROM read_parquet('s3://bucket/data.parquet')" \
  -o result.csv

# Load SQL from a file
dtpipe -i pg:... -q @queries/export.sql -o export.parquet

# Environment variables
dtpipe -i "pg:Host=${{DB_HOST}};Database=${{DB_NAME}}" -q "SELECT 1" -o out.csv

# Composable: keyring value containing env var placeholders
dtpipe secret set s3-init "LOAD httpfs; SET s3_region='${{AWS_REGION}}';"
dtpipe -i duck:memory --duck-init "keyring://s3-init" ...
# → loads the block, then substitutes ${{AWS_REGION}}
```

---

## Providers

| Provider | Input | Output | Prefix | Requires query | Stdin/Stdout | Notes |
|:---|:---:|:---:|:---|:---:|:---:|:---|
| **DuckDB** | ✅ | ✅ | `duck:` | ✅ | — | `--duck-init` supported |
| **SQLite** | ✅ | ✅ | `sqlite:` | ✅ | — |
| **PostgreSQL** | ✅ | ✅ | `pg:` | ✅ | — |
| **Oracle** | ✅ | ✅ | `ora:` | ✅ | — |
| **SQL Server** | ✅ | ✅ | `mssql:` | ✅ | — |
| **CSV** | ✅ | ✅ | `csv:` / `.csv` | — | ✅ |
| **JsonL** | ✅ | ✅ | `jsonl:` / `.jsonl` | — | ✅ |
| **XML** | ✅ | — | `xml:` / `.xml` | — | ✅ |
| **Apache Arrow** | ✅ | ✅ | `arrow:` / `.arrow` | — | ✅ |
| **Parquet** | ✅ | ✅ | `parquet:` / `.parquet` | — | ✅ |
| **S3 object storage** | ✅ | ✅ | `s3://` / `s3a://` | — | — | See [Object storage](#object-storage-s3-azure) |
| **Azure Blob** | ✅ | ✅ | `azure://` / `az://` | — | — | See [Object storage](#object-storage-s3-azure) |
| **Data Gen** | ✅ | — | `generate:N` | — | — |
| **Null** | — | ✅ | `null:` | — | — |
| **Checksum** | — | ✅ | `checksum:` | — | — |

> For Stdin/Stdout: use `-` as the connection string (`csv:-`) or the bare provider name (`csv` = `csv:-`).

> **DuckDB dual role**: Beyond being a regular read/write provider, DuckDB also serves as the **internal SQL engine** for `--sql` branches in DAG pipelines (joins, unions, CTEs). See [DAG Syntax](#dag-syntax) and [Provider-Specific Options](#provider-specific-options) for details on `--duck-init`.

### Database Connection Strings (ADO.NET format)

DtPipe is powered by .NET database providers, which expect standard **ADO.NET connection strings** rather than the connection URIs typically used in the Python/data ecosystem (e.g. by SQLAlchemy or psycopg2).

* **ADO.NET format**: A list of semicolon-separated `Key=Value;` pairs (e.g. `Host=localhost;Database=mydb;`).
* **Python URIs** (e.g. `postgresql://user:pass@host:port/db`) are **not natively supported** by the underlying database drivers and must be translated.

For a comprehensive catalog of all connection string options, parameters, and database drivers, visit **[connectionstrings.com](https://www.connectionstrings.com/)**.

#### Conversion Reference Table

If you are coming from Python or SQLAlchemy, use this translation guide to build your `-i` / `-o` strings:

| Database | Prefix | Python URI Format | ADO.NET Format (DtPipe) |
|:---|:---|:---|:---|
| **PostgreSQL** | `pg:` | `postgresql://user:pass@host:port/db` | `pg:Host=host;Port=port;Database=db;Username=user;Password=pass` |
| **SQL Server** | `mssql:` | `mssql+pyodbc://user:pass@host/db` | `mssql:Server=host;Database=db;User Id=user;Password=pass;TrustServerCertificate=True` |
| **SQLite** | `sqlite:` | `sqlite:///path/to/file.db` | `sqlite:Data Source=path/to/file.db` |
| **Oracle** | `ora:` | `oracle+oracledb://user:pass@host:port/?service_name=service` | `ora:Data Source=host:port/service;User Id=user;Password=pass` |

---

## Source (Reader) Options

| Flag | Example | Description |
|:---|:---|:---|
| `--connection-timeout` | `30` | Connection timeout in seconds |
| `--query-timeout` | `0` | Query timeout in seconds (0 = no timeout) |
| `--unsafe-query` | | Allow non-SELECT queries (stored procs, etc.) |
| `--csv-separator` | `","` | CSV field separator |
| `--csv-has-header` | | CSV has a header row (default: true) |
| `--encoding` | `ISO-8859-1` | Text file encoding |
| `--column-types` | `"Id:uuid,Qty:int32"` | Explicit column type declarations for text readers |
| `--auto-column-types` | | Infer column types from the first 100 rows |
| `--path` | `"//Product"` | XPath / JSON path for record selection (XML, JsonL) |
| `--query` / `-q` | `"SELECT ..."` | SQL query executed by database readers (or a path to a `.sql` file) |
| `--table` / `-t` | `"users"` | Source table name — auto-builds `SELECT * FROM "<table>"` when no `--query` is given (database readers) |
| `--duck-init` | `"LOAD httpfs"` | **(DuckDB only)** SQL executed after connection open. See [Value Resolution](#value-resolution) |

---

## Data Transformations

Transformers execute in left-to-right order. Consecutive flags of the same type are grouped
into one step; a different flag type starts a new step.

```
--fake A --fake B --format C --fake D
→  FakeTransformer(A, B) → FormatTransformer(C) → FakeTransformer(D)
```

| Flag | Syntax | Description |
|:---|:---|:---|
| `--fake` | `"Col:dataset.method"` | Generate fake data via [Bogus](https://github.com/bchavez/Bogus) |
| `--fake-locale` | `fr` | Locale for fake data generation |
| `--fake-seed` | `12345` | Global seed for reproducible random fakes (also acts as a base offset for deterministic row/column faking) |
| `--fake-seed-column` | `"UserId"` or `"Region,Branch"` | Column(s) used as a deterministic seed (same input -> same output). Supports comma-separated columns for composite seeds. |
| `--fake-seed-row` | | Row-index based deterministic mode (row N always gets the same values). Formerly `--fake-deterministic` (deprecated, throws error). |
| `--skip-null` | | Skip fake generation when the source value is null |
| `--mask` | `"Phone:###-****"` | Partial masking (`#` keeps original char, any other replaces) |
| `--null` | `"ColName"` | Force a column to NULL |
| `--overwrite` | `"Status:Active"` | Set a static value for every row in a column |
| `--format` | `"Display:{First} {Last}"` | [.NET Composite Format](https://learn.microsoft.com/en-us/dotnet/standard/base-types/composite-formatting) using column names as placeholders |
| `--compute` | `"Col:row.A * 2"` | JS expression. Implicit return for single expressions; use `return` with statements |
| `--compute-types` | `"Col:int32"` | Declare the CLR type of a computed or new column |
| `--filter` | `"row.Val > 100"` | Drop rows where the JS expression returns falsy |
| `--expand` | `"row.Tags.split(',')"` | Expand one row into multiple (must return an array of objects) |
| `--window-count` | `5` | Window size for stateful batch processing |
| `--window-script` | `"rows.map(...)"` | JS logic executed over a sliding window of rows |
| `--ignore-nulls` | | Skip transformations when the input cell is NULL |

---

## Schema & Projection

| Flag | Syntax | Description |
|:---|:---|:---|
| `--rename` | `"OldName:NewName"` | Rename a column |
| `--project` | `"Id,Name,Email"` | Keep only these columns (whitelist) |
| `--drop` | `"InternalId"` | Remove a column (blacklist) |

---

## Target (Writer) Options

| Flag | Syntax | Description |
|:---|:---|:---|
| `--strategy` | `Append` | Write strategy. One of: `Append`, `Truncate`, `DeleteThenInsert`, `Recreate`, `Upsert`, `Ignore` |
| `--table` | `"users"` | Override target table name (default: `export`) |
| `--key` | `"Id,Code"` | Primary key column(s) for `Upsert`/`Ignore`. Auto-detected from DB if omitted |
| `--insert-mode` | `Bulk` | `Standard` or `Bulk` (high-speed batch insert for PG, Oracle, MSSQL) |
| `--auto-migrate` | | `ALTER TABLE` to add missing columns automatically |
| `--strict-schema` | | Reject rows that don't match the target schema |
| `--no-schema-validation` | | Disable schema validation entirely |
| `--pre-exec` | `"TRUNCATE ..."` | SQL script to run **before** the pipeline starts |
| `--post-exec` | `"ANALYZE ..."` | SQL script to run **after** a successful transfer |
| `--on-error-exec` | `"..."` | SQL script to run on pipeline error |
| `--finally-exec` | `"..."` | SQL script to run regardless of outcome |
| `--prefix` | `"staging_"` | Table name prefix applied to all DB writers |
| `--duck-init` | `"LOAD azure"` | **(DuckDB only)** SQL executed after connection open. See [Value Resolution](#value-resolution) |

> `--pre-exec`, `--post-exec` etc. accept inline SQL or a file path (`@scripts/pre.sql` or a `.sql` file path).
> `--duck-init` runs on the DuckDB connection before reads or writes (unlike `--pre-exec` which runs on the target DB after connection).

### Object storage (`s3://`, `azure://`)

Object-storage locations are first-class inputs and outputs. They go through the DuckDB engine
already in the process, so globs, range requests and multipart uploads work without any extra
dependency. Reads stream: no object is downloaded to a temp file first. Writes stage the rows
before uploading (a Parquet footer is only known at the end), spilling to the temp directory if
the output exceeds memory.

```bash
dtpipe -i s3://bucket/events/2026-08-*.parquet --s3-region eu-west-1 -o events.csv
dtpipe -i sales.csv -o azure://reports/sales.parquet --azure-connection-string "${{keyring://azure-conn}}"
```

| Provider | Schemes | Options |
|:---|:---|:---|
| `s3` | `s3://`, `s3a://` | `--s3-endpoint`, `--s3-region`, `--s3-access-key`, `--s3-secret-key`, `--s3-session-token`, `--s3-url-style` |
| `azure` | `azure://`, `az://` | `--azure-connection-string`, `--azure-account-name`, `--azure-account-key`, `--azure-sas`, `--azure-endpoint` |

- **Format** comes from the extension, through a closed map: `.parquet`, `.csv`, `.tsv`, `.json`,
  `.jsonl`, `.ndjson`. Anything else is an error naming the supported set — no content sniffing.
  For a format outside that map, use `--duck-init` + `--query` with the matching DuckDB function.
- **Credentials** accept the usual value forms (`${{keyring://alias}}`, `${{ENV_VAR}}`, `@file`).
  Leave the key pair unset to use the ambient credential chain (env, shared config, instance
  profile). Secrets are scoped to their bucket/container, so a read and a write in the same
  pipeline can use different credentials.
- **Writes replace the target key.** Object storage has no append or upsert, so `--strategy` does
  not apply. The upload is issued once the pipeline completes: a failed run leaves the existing
  object untouched rather than replacing it with a partial one.
- **Reads glob natively**: `s3://bucket/dt=*/part-*.parquet` reads every match.
- `https://`, `gs://` and other schemes are **not** claimed by any provider, and object storage is
  never a hub target (`duck+s3:` fails closed). Reach those through the DuckDB engine
  (`--duck-init "INSTALL httpfs; …"` + `read_parquet(…)` / `COPY … TO …`) — see the MinIO/Azurite
  scenarios in `tests/scripts/validate_duck_hub.sh`.
- Object-storage connection strings count as network access for the agent guardrails: a job using
  one needs `--allow-network` (see [Agent Guardrails](#agent-guardrails)).
- The `httpfs` / `azure` extensions are installed from DuckDB's extension repository on first use,
  so the host needs access to it once (or an extension directory already holding them).

### DuckDB Hub connections (`duck+{provider}:`)

The hub prefix means `ATTACH`: it integrates another database as a SQL catalog inside the DuckDB
instance already running in-process. It is relational only — a catalog, not a file transport.

| Hub prefix | Extension | ATTACH type |
|:---|:---|:---|
| `duck+mysql:` | `mysql` | `TYPE MYSQL` |

PostgreSQL and SQLite are intentionally not hub targets: the native `pg:`/`postgres:` and `sqlite:`
providers already cover them with capability an ATTACH catalog cannot reach (COPY, bulk load,
upsert), so routing them through the hub would be strictly inferior. `duck+pg:`, `duck+postgres:`,
`duck+postgresql:`, and `duck+sqlite:` fail with the supported list, same as any unrecognized
provider. MySQL stays because no native provider exists yet.

Any other provider fails with the supported list rather than forwarding an unverified name into the
`TYPE` clause. Other DuckDB extensions (`excel`, `httpfs`, `azure`, `ducklake`…) are reached through
`--duck-init` + `--query` / `--post-exec`, not through the hub prefix.

The connection string must carry an explicit database name (`Database=`, `DbName=`, or `Db=`),
which becomes the ATTACH alias. Without one, parsing fails closed rather than guessing an alias —
falling back to the bare provider name let two ATTACHes in the same process (e.g. one input, one
output) collide on the same alias and silently `USE` the wrong catalog.

> Extensions are `INSTALL`ed on first use, which needs network access to the DuckDB extension
> repository unless they are already present in the local extension directory.

---

## Execution & Statistics

| Flag | Syntax | Description |
|:---|:---|:---|
| `--limit` | `1000` | Stop after N rows have been processed |
| `--sampling-rate` | `0.1` | Row inclusion probability (0.0–1.0) |
| `--sampling-seed` | `12345` | Fixed seed for deterministic, reproducible sampling |
| `--batch-size` | `10000` | Rows per columnar batch (default: 50,000) |
| `--no-stats` | | Suppress progress bars and transfer statistics |
| `--metrics-path` | `metrics.json` | Write structured execution results to a JSON file |
| `--log` | `pipeline.log` | Write log output to a file |
| `--strict-bindings` | | Fail with a non-zero exit code on unrecognized flags or failed option bindings instead of skipping them silently (default: warn/skip) |

---

## DAG Syntax

### Options

| Option | Description |
|:---|:---|
| `--alias NAME` | Name the current branch for downstream reference |
| `--from ALIAS[,ALIAS...]` | Streaming source(s). Fan-out uses a single alias; multi-stream processors use comma-separated aliases |
| `--ref ALIAS[,ALIAS...]` | Materialized reference source(s) — fully preloaded before query execution. Use for JOIN lookups |
| `--sql "QUERY"` | Inline SQL executed by the internal DuckDB engine (standard SQL, window functions, CTEs, JSON) |
| `--duck-init "SQL"` | SQL to run on the DuckDB SQL processor connection after open (e.g. `LOAD httpfs`). See [Value Resolution](#value-resolution) |
| `--merge` | UNION ALL of all `--from` sources. Requires at least 2 streaming sources |

> **`--ref` is intentionally materialized.** Secondary sources declared via `--ref` are read fully
> into memory so the query engine can build a cost-based plan. Only the `--from` source streams.

#### Implicit branch-split rules

Branches are separated implicitly while walking the arguments. One pure function
(`BranchSplitDecision`) decides; the triggers are:

| Token | Splits when |
|:---|:---|
| `-i` / `--input` | an input **or** job file was already seen in the current branch |
| `--from` | a `--from`, `--input` or `--job` was already seen — the first `--from` in a fresh branch stays in the current branch |
| `--job` / `-j` | a job file **or** input was already seen |
| *positional SQL text* | a bare (non-flag) token starts a new branch that becomes the `--sql` processor branch |

Neither `--sql` nor boolean processor flags (e.g. `--merge`) trigger a split.
> Pre-filter large lookup tables upstream before using them as `--ref`.

#### Duplicate flags are an error

A non-repeatable flag may appear **at most once per stage** within a branch — stages being
reader (before the first transformer), pipeline (transformer scope) and writer (after `-o`).
A repeated flag in the same stage is a hard error, not a silent last-wins:

```
# ERROR: --sql provided twice in the same branch
dtpipe -i src.csv --alias s --from s --sql "SELECT * FROM s" --sql "SELECT count(*) FROM s" -o out.csv

# OK: same flag in two different stages = two independent bindings
dtpipe -i in.csv --csv-separator ";" -o out.csv --csv-separator "|"
```

Global scalar flags (`--log`, `--metrics-path`, …) may appear only once per command line.
The SQL query of a branch must come from exactly one source: an explicit `--sql "<query>"`
**or** one positional query — combining both is an error.

Transformer options are the exception: a new transformer **instance** starts at every
trigger-flag recurrence (`--fake A --fake-seed-row --fake B --fake-seed-row` builds two
instances), so repeating a transformer option configures the next instance and is legal.

> **SQL engine**: The `--sql` processor uses DuckDB internally — the same engine available as a read/write provider (`duck:`). This means all DuckDB SQL extensions and functions are available in `--sql` branches. Use `--duck-init` to load extensions before query execution. See [Provider-Specific Options](#provider-specific-options) for details.

### Canonical topologies

| Topology | Pattern |
|:---|:---|
| **Linear** | `-i {src} -o {dst}` |
| **Two independent sources** | `-i {src1} -o {dst1}  -i {src2} -o {dst2}` |
| **SQL (single source)** | `-i {src} --alias a  --from a --sql "SELECT * FROM a" -o {dst}` |
| **SQL JOIN (main + ref)** | `-i {main} --alias m  -i {ref} --alias r  --from m --ref r --sql "SELECT * FROM m JOIN r ON ..."` |
| **Merge (UNION ALL)** | `-i {srcA} --alias a  -i {srcB} --alias b  --from a,b --merge -o {dst}` |
| **Fan-out (tee)** | `-i {src} --alias s  --from s -o {dstA}  --from s -o {dstB}` |
| **Fan-out + SQL** | `-i {src} --alias s  --from s -o {dstA}  --from s --sql "SELECT ..."` |
| **Diamond** | `-i {src} --alias s  --from s --filter '...' --alias hi  --from s --filter '...' --alias lo  --from hi --ref lo --sql "..."` |
| **Join → fan-out** | `... --from m --ref r --sql "..." --alias j  --from j -o {dstA}  --from j -o {dstB}` |

---

## YAML Job File Schema

### Minimal example

```yaml
main:
  input: "pg:Host=localhost;Database=prod;Username=postgres"
  output: "output.parquet"
  provider-options:
    pg:
      query: "SELECT * FROM users"
```

### Full structure

```yaml
branch-name:
  # I/O
  input: "..."
  output: "..."

  # Engine controls (all optional — defaults shown)
  batch-size: 50000
  limit: 0
  sampling-rate: 1.0
  sampling-seed: null
  dry-run-count: 0
  metrics-path: null
  log-path: null
  prefix: null

  # DAG routing (optional)
  from: "upstream-alias"
  ref:
    - "ref-alias"

  # Transformer pipeline (optional)
  transformers:
    - fake:
        mappings:
          name: name.fullName
          email: internet.email
        options:
          locale: fr
          seed: 12345
          seed-column: id
          deterministic: true
          skip-null: true
    - null:
        mappings:
          phone: ~
    - compute:
        compute:
          - "FullName:row.Name + ' ' + row.Surname"
    - filter:
        filter: "row.Active"
    - project:
        mappings:
          id: ~
          name: ~
          email: ~

  # Provider-specific options (keyed by component name)
  provider-options:
    pg:                      # applies to pg reader
      query: "SELECT * FROM users"
    csv-writer:              # applies to csv writer only (suffix -writer or -reader)
      separator: ";"
      quote: "'"
```

### Transformer YAML reference

| Transformer | YAML key structure | Notes |
|:---|:---|:---|
| `fake` | `mappings: {col: dataset.method}` + `options: {locale, seed, seed-column, deterministic, skip-null}` | |
| `null` | `mappings: {col: ~}` | Value is ignored |
| `overwrite` | `mappings: {col: value}` | |
| `mask` | `mappings: {col: pattern}` | `#` keeps, any other char replaces |
| `format` | `mappings: {col: "{A} {B}"}` | .NET composite format |
| `compute` | `compute: ["col:expression", ...]` | JS expressions list |
| `filter` | `filter: "expression"` | JS boolean expression |
| `expand` | `expand: "expression"` | Must return an array of objects |
| `window` | `mappings: {script: "..."}` + `options: {count: N}` | |
| `project` | `mappings: {col: ~}` | Listed columns are kept |
| `drop` | `mappings: {col: ~}` | Listed columns are removed |
| `rename` | `mappings: {OldName: NewName}` | |

### Environment variable and secret interpolation

Environment variables and secrets use the `${{...}}` syntax. See [Value Resolution](#value-resolution) for the full compatibility matrix and CLI/YAML differences.

---

## Provider-Specific Options

### DuckDB

DuckDB serves a dual role in dtpipe: it is both a standard read/write **provider** (`duck:`) and the **internal SQL engine** powering `--sql` branches. The `--duck-init` flag applies to all three integration points:

| Component | Flag | When it runs |
|:---|:---|:---|
| Reader (`duck:`) | `--duck-init` | After connection open, before query |
| Writer (`duck:`) | `--duck-init` | After connection open, before schema initialization |
| SQL processor (`--sql`) | `--duck-init` | After connection open and built-in `SET` statements, before Arrow stream registration |

> `--pre-exec` / `--post-exec` run SQL **on the target database after writes**; `--duck-init` runs **on the DuckDB connection before reads or queries**. They serve different purposes and can be combined.

In YAML job files, use the `provider-options` block keyed by component name:

```yaml
provider-options:
  duck:           # reader
    duck-init: "LOAD httpfs; SET s3_region='eu-west-1';"
  duck-writer:    # writer
    duck-init: "keyring://azure-init"
```

For a `--sql` branch, pass `--duck-init` alongside `--from` and `--sql` on the same branch:

```bash
dtpipe -i events.parquet --alias ev \
  --from ev \
  --duck-init "LOAD httpfs; SET s3_region='${{keyring://s3-region}}';" \
  --sql "SELECT * FROM ev JOIN read_parquet('s3://bucket/ref.parquet') r ON ev.id = r.id" \
  -o result.parquet
```

In a YAML job, the same branch nests both under `provider-options.sql` (not `provider-options.duck` — that key belongs to the reader/writer):

```yaml
ev:
  input: "events.parquet"
enrich:
  from: "ev"
  provider-options:
    sql:
      query: "SELECT * FROM ev JOIN read_parquet('s3://bucket/ref.parquet') r ON ev.id = r.id"
      duck-init: "LOAD httpfs; SET s3_region='${{keyring://s3-region}}';"
  output: "result.parquet"
```

## Incremental Loading

DtPipe supports cursor-driven incremental loading to transfer only new or updated records since the last successful run.

### Overview

Incremental loading uses two key mechanisms:
1. **State Persistence**: The CLI tracks the maximum value observed in a designated cursor column and writes it to a JSON state file after a successful execution.
2. **Query Interpolation**: The SQL query uses the `${{cursor://path|default}}` resolver to filter for records greater than (or equal to) the last saved value.

### CLI Flags

- `--cursor COLUMN` — Specifies the column to observe for tracking the maximum value (e.g. `updated_at` or `id`).
- `--state PATH` — Specifies the path to the state file where the cursor metadata will be saved.
- `--cursor-from VALUE` — Global override to temporarily force a starting cursor value for the current run, ignoring the state file.

### State File Format

The state file is stored as a simple, human-readable JSON file:
```json
{
  "version": 1,
  "cursor": {
    "column": "updated_at",
    "value": "2026-06-15T23:59:59.000",
    "type": "datetime"
  },
  "last_run": {
    "started_at": "2026-06-16T02:00:00Z",
    "completed_at": "2026-06-16T02:03:42Z",
    "rows_transferred": 1234,
    "status": "success"
  }
}
```

### DAG Validation

To prevent concurrent writes or corrupted cursor states, DtPipe enforces that **no two writers may share the same state file**. If the DAG validator detects duplicate state files across branches, pipeline execution will fail immediately.

---

## Shell Completion

```bash
dtpipe completion --install   # installs for bash, zsh, or PowerShell
```

Restart your terminal (or `source ~/.zshrc`) to activate. Completion suggests providers
(`pg:`, `csv:`…) strategies (`Append`, `Upsert`…), and flag names based on cursor position.

---

## Model Context Protocol (MCP) Server

`dtpipe mcp` starts a native Model Context Protocol (MCP) server over STDIO. This allows AI coding agents and assistants (Cursor, Claude Desktop, Antigravity, VS Code MCP) to interact with `dtpipe` directly.

### Usage

```bash
dtpipe mcp
```

### Exposed MCP Tools

| Tool Name | Parameters | Description |
|:---|:---|:---|
| `list-providers` | *(none)* | List all registered data readers, transformers, and writers |
| `inspect` | `input`, `query?` | Inspect data source schema or auto-discover database tables if query is omitted |
| `preview-data` | `input`, `limit?`, `query?` | Preview sample data rows (default: 5 rows) |
| `validate-yaml-job` | `yamlContent` | Validate YAML job topology and syntax without running |
| `execute-yaml-job` | `yamlContent`, `apply?`, `allowDestructive?`, `allowNetwork?` | Execute a YAML job in-memory. **Dry-run by default** (no write); `apply=true` performs a write, gated by the approval gate and the SQL safety policy |
| `dry-run` | `yamlContent` | Validate, open the reader, report schema/estimated count without writing |
| `help` | *(none)* | General usage guidelines, YAML job structures, and DAG topology rules |
| `get-adapter-help` | `adapterName` | Inspect connection string format, reader/writer options, and YAML examples for an adapter |
| `get-transformer-help` | `transformerName` | Inspect options, mapping syntax, and YAML examples for a transformer |
| `get-anonymization-help` | *(none)* | Inspect Bogus faker datasets, methods, and options for anonymization |
| `register-yaml-job` | `name`, `yamlContent` | Register a YAML job configuration in memory to obtain a virtual `memory://` URI |

---

### AI Agent Subcommand (`dtpipe agent`)

Launches an interactive or automated ReAct AI agent loop for data integration tasks. The agent runs against a local Ollama install or the OpenAI API (`--provider`), auto-discovers local Ollama models, renders compact TUI status lines, provides an interactive step inspector (trajectory viewer), renders Spectre.Console DAG topology boxes, and offers 1-click YAML file exports.

```bash
dtpipe agent [<prompt>] [options]
```

| Option | Alias | Description | Default |
|:---|:---|:---|:---|
| `<prompt>` | `-p`, `--prompt` | Task description for the AI agent | *(Interactive prompt if omitted)* |
| `--provider` | | LLM provider: `ollama` (local) or `openai` | `ollama` |
| `--api-key` | | API key for the `openai` provider. Falls back to the `DTPIPE_LLM_API_KEY` environment variable | *(unset)* |
| `--model` | `-m` | Model name, provider-dependent (e.g. `gemma4:12b-mlx`, `qwen2.5-coder:7b` for Ollama; `gpt-4o` for OpenAI) | *(Auto-discovered from Ollama)* |
| `--url` | `-u` | API endpoint URL | `http://localhost:11434` (ollama) · `https://api.openai.com` (openai) |
| `--max-iterations` | | Maximum ReAct loop iterations per turn | `25` |
| `--interactive` | `-i` | Force interactive model selection and prompt entry | `false` |
| `--mode` | | Operating mode: `plan` designs/validates only (no execution); `execute`/`autonomous` may run through the guardrails | `plan` |
| `--temperature` | | Sampling temperature; `0` makes decoding deterministic | `0` |
| `--seed` | | Fixed seed for reproducible sampling | `0` |
| `--repeat` | | Replicate the validated plan N times and report determinism variance | `1` |
| `--sequential` | | Execute tool calls one at a time instead of running independent calls in parallel | `false` |
| `--apply` | | Perform a real write (a write also requires an approving gate and a clean SQL safety check) | `false` (dry-run) |
| `--allow-destructive` | | Allow destructive SQL verbs (`DROP`/`DELETE`/`TRUNCATE`/`UPDATE`/`ALTER`/`INSERT`/`ATTACH`) | `false` |
| `--allow-network` | | Allow network access in SQL (`LOAD httpfs`/`azure`, remote `read_parquet`/`read_csv`) | `false` |

> **Hardening (fail-closed defaults).** With no flags, `dtpipe agent` is the safest behavior:
> mode `plan`, temperature `0` + seed (deterministic), dry-run only, destructive SQL and network
> access denied. A real write requires `--apply` **and** approval **and** a compliant SQL safety
> check. The planner never sees the `execute-yaml-job` tool; execution is a deterministic engine
> step. The `yamlContent` tool argument is the sole source of the plan YAML. Inspected schemas/
> samples/errors survive conversation compaction (non-destructive context). See the **Agent
> Guardrails** section for the SQL safety policy detail.

---

### Agent Guardrails (`ISqlSafetyPolicy` / `IApprovalGate`)

The agent and `execute-yaml-job` are **fail-closed**: when in doubt, the behavior *rejects* rather
than executes. Every unlock flag is documented so nothing is implicit.

- **Dry-run by default.** `execute-yaml-job` writes **nothing** unless `apply=true`. Even then a
   write requires (a) the `--apply` operator consent, (b) an approving `IApprovalGate`, and
   (c) a clean SQL safety check. Non-interactive contexts (MCP/agent) default to **deny** for
   writes — a human `--apply` is the consent that unblocks.
- **SQL safety policy** (`DefaultSqlSafetyPolicy`). Fails closed on:
   - Destructive verbs: `DROP`, `DELETE`, `TRUNCATE`, `UPDATE`, `ALTER`, `INSERT`, `ATTACH`
      → unblock with `--allow-destructive`.
   - Network access: `LOAD httpfs` / `LOAD azure`, and `read_parquet`/`read_csv`/`read_json`
     over `http(s)://`, `s3://`, `ftp://` or `gs://` → unblock with `--allow-network`.
   - Pure `SELECT`-style reads always pass.
- **Approval gate** (`DefaultApprovalGate`). A real write is approved only when `apply` is set
   **and** either the context is interactive or an override predicate grants it. Non-interactive
   ⇒ write denied (read-only).
- **Determinism.** `--temperature 0 --seed N --repeat 3` replicates a validated plan and reports
   variance (distinct-YAML count − 1); `0` ⇒ byte-for-byte reproducible.
- **Non-destructive context.** Inspected schemas, sample rows and recent errors are cached in an
   `AgentContextStore` and reloaded into the compacted window instead of the lossy one-line
   summary; the full journal is always kept in the trajectory. KISS — no mandatory second LLM
   call.
- **Planner/executor split.** In `--mode plan` the `execute-yaml-job` tool is *removed from the
   tool list* the model sees; the LLM cannot drive execution. Execution is a deterministic
   engine step (`JobService.ExecutePipelineAsync` on the validated plan).
- **Parallel tools.** Every tool call the model emits in a turn is executed (independent ones in
   parallel via `Task.WhenAll`); `--sequential` forces one-at-a-time. Each call yields one
   `tool` message correlated by call id.
- **CI gate.** `tests/agentic/analyze-traces.sh --gate` fails when (a) unhandled MCP errors,
   (b) determinism variance above threshold, or (c) a mission failed.
   `tests/agentic/run-all.sh --gate` propagates it. See `.github/workflows/agentic-ci.yml`.


