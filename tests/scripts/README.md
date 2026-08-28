# Integration Validation Scripts

This directory contains a suite of Bash scripts used to validate DtPipe functionality end to end. These tests go beyond unit testing by verifying the actual binary execution, file system interaction, and multi-database connectivity.

## Infrastructure Management

DtPipe uses a centralized Docker infrastructure for all integration tests.

- **Shared Infrastructure**: All database and storage containers (Postgres, MSSQL, Oracle, MySQL, MinIO S3) are defined in [tests/infra/docker-compose.yml](../infra/docker-compose.yml).
- **Startup & Health**: Scripts call [start_infra.sh](../infra/start_infra.sh) which ensures all services are not just running, but fully ready for connections.
- **Persistence**: Containers persist after scripts finish. Use [stop_infra.sh](../infra/stop_infra.sh) if you need a full cleanup.

## Quick Start

```bash
# Run everything (smoke + test-docker + catalog + bench)
./tests/scripts/run.sh --full

# No Docker: transformers, schema, options, hooks, docs, DAG topologies
./tests/scripts/run.sh --test

# Docker required: above + driver chain + upsert/ignore + DuckDB hub
./tests/scripts/run.sh --test-docker

# 135-command catalog suite (Docker + init_test_data.sh required)
./tests/scripts/init_test_data.sh
./tests/scripts/run.sh --catalog

# DAG topologies only (no Docker)
./tests/scripts/run.sh --dag
```

## Master Runner

**`run.sh`** — Orchestrates all suites. Modes:

| Flag | What runs | Docker? |
|:---|:---|:---|
| `--smoke` | Golden smoke test: edge cases, 1M rows, all DB drivers | Yes |
| `--test` | Transformers, schema, options, hooks, docs, DAG | No |
| `--test-docker` | All `--test` suites + driver chain (upsert/ignore/cross-DB) + DuckDB Hub | Yes |
| `--catalog` | 135-command catalog (requires `init_test_data.sh` first) | Yes |
| `--dag` | DAG topology validation only | No |
| `--bench` | Performance benchmarks (linear pipeline, DuckDB) | No |
| `--full` | All of the above | Yes |

## Scripts Index

### 🔥 Smoke & Drivers
| Script | Description | Docker? |
|:---|:---|:---|
| **`smoke.sh`** | Vicious edge cases (CSV escaping, SQL injection, NULL, UTF-8), 1M rows, composite-key upsert on all DB drivers. | Yes |
| **`validate_drivers.sh`** | Read/write for all drivers, Upsert/Ignore strategies, cross-driver chain (CSV→PG→MSSQL→Oracle→Parquet), Oracle insert modes. | Yes |
| **`validate_duck_hub.sh`** | DuckDB Extender & Hub (`duck+{provider}:`): SQLite, Postgres, MySQL, and MinIO S3 Object Storage (`httpfs` / `duck+s3:`). | Yes |

### 🧪 Feature Validation (no Docker)
| Script | Validates |
|:---|:---|
| **`validate_transformers.sh`** | All 13 row transformers: Overwrite, Null, Mask, Fake, Format, Compute, Drop, Project, Rename, Filter, Expand, Window, ordering. |
| **`validate_schema.sh`** | Strict-schema rejection, `--no-schema-validation` bypass, `--auto-migrate` for SQLite and Postgres. |
| **`validate_options.sh`** | Provider option scoping (global/writer/YAML), sampling rate + seed + determinism, YAML `provider-options`, `--metrics-path`. |
| **`validate_hooks.sh`** | `--pre-exec` (inline + file), `--post-exec`, `--finally-exec` lifecycle hooks via SQLite. |
| **`validate_cursor.sh`** | Incremental loading: `--cursor`, `--state`, `${{cursor://...}}` value resolution, state file lifecycle. |
| **`validate_docs.sh`** | All `--flags` in README/COOKBOOK are present in `--help`; representative README examples execute correctly. |
| **`validate_dag.sh`** | All 9 canonical DAG topologies: Linear, Two-source, SQL, SQL JOIN, Fan-out, Fan-out+SQL, Diamond, Join→fan-out, Nested data. |

### 📋 Catalog Suite
| Script | Description | Docker? |
|:---|:---|:---|
| **`run_catalog_tests.sh`** | 135 numbered commands covering the full feature surface: adapters, DAG patterns, transformers, volumetrics, error cases, real-world scenarios. Requires `init_test_data.sh`. | Yes |
| **`init_test_data.sh`** | Provisions all data sources (CSV, Parquet, Arrow, DuckDB, PG, MSSQL, Oracle) used by the catalog suite. Idempotent. | Yes |
| **`clean_test_data.sh`** | Removes all provisioned artifacts for a full reset. | No |

### 📊 Benchmarks
| Script | Target |
|:---|:---|
| **`bench.sh`** | Linear pipeline throughput (100k→CSV, CSV→Parquet, Parquet+transforms), DuckDB 1M rows. |
| **`benchmark_dtpipe_columnar.sh`** | Zero-copy columnar path performance. |
| **`generate_benchmark_datasets.sh`** | Generates large Parquet/CSV datasets for JOIN benchmarks. |
| **`micro_perf_gate.sh`** | Micro performance gate — BenchmarkDotNet in-process on the hot conversion paths, compared against a versioned baseline. Runs in CI on every push. |

#### The three-tier performance gate

| Tier | What | Where | Threshold |
|:---|:---|:---|:---|
| **Micro** | `micro_perf_gate.sh` — BenchmarkDotNet, no infrastructure | CI, every push | Wide (detects a ×2) |
| **Macro complete** | 15 scenarios incl. Oracle and SQL Server | Local only, `dtpipe-sandbox` repo, tag ritual | 15 % |
| **Macro light** | file↔file + PostgreSQL subset | Optional, nightly — only if micro proves insufficient | Wide |

The complete macro tier stays out of CI for two independent reasons. The practical
one: free runners cannot host Oracle and SQL Server containers. The methodological
one, which holds even if that changed: a shared cloud runner has 20-50 % duration
variance, so a 15 % gate there produces random red rather than signal.

**Baselines carry a machine fingerprint and the gate refuses to compare across two.**
Comparing durations measured on different hardware does not give a weaker verdict, it
gives a misleading one. `--allow-foreign-host` overrides the refusal and clamps the
threshold to no tighter than 50 % — which is exactly what CI passes, since the
committed baseline was recorded on the reference machine and every runner is foreign.

The baseline is `tests/DtPipe.Benchmarks/baselines/micro_perf.json` — it lives with the
benchmark project, not in `tests/scripts/baselines/`, which holds golden *data* fixtures.

```bash
# Record a baseline on this machine
./tests/scripts/micro_perf_gate.sh --update

# Compare (strict — refuses a foreign host)
./tests/scripts/micro_perf_gate.sh

# Just print the numbers, compare nothing
./tests/scripts/micro_perf_gate.sh --report-only
```

Exit codes: `0` pass · `1` regression · `2` refused to render a verdict · `3` setup error.

### 🛠️ Utilities
| Script | Description |
|:---|:---|
| **`monitor_mem.sh`** | Memory usage monitoring helper. |

---

## Artifacts

Temporary files land in `tests/scripts/artifacts/`. Scripts clean up on success; failures preserve artifacts for debugging.
