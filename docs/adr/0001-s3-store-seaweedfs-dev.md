# ADR-0001: SeaweedFS as dev/on-prem S3 store instead of MinIO
Status: accepted (2026-09-25)
Context: D5 requires an S3-API-only object store. MinIO no longer publishes public container images (Docker Hub / quay pulls denied), so `docker compose up` cannot rely on it.
Decision: Application code targets the S3 API only (`DAM_S3_*`). Dev compose uses SeaweedFS (Apache-2.0, multi-arch). Production may use AWS S3, Azure via gateway, or any S3-compatible store.
Consequences: Object lock/KMS/tiering must be validated per backend in Step 4.4/4.5; if on-prem needs MinIO, build the image from source.
