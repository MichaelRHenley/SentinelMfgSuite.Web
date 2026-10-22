# Issued report PDF storage (Stage 3d-5)

Issued assessment report PDFs are rendered from frozen revision data and stored in **private, write-once filesystem
storage**. The database row (`AssessmentReportRevisions.PdfStorageKey`, `PdfSha256`, `PdfLengthBytes`) is the only
reference to an object. There are no public URLs, static-file mappings or download endpoints.

## Configuration

| Setting | Required | Notes |
|---|---|---|
| `ReportPdfStorage:RootPath` | Production: **yes** | Absolute directory path. Set via environment variable `ReportPdfStorage__RootPath` or secure configuration, not source control. |

Rules enforced at first use (the service fails fast with a clear error):

- Production (any non-Development environment) must configure the setting; there is no default.
- The path must be absolute and must **not** be inside, or contain, the application content root. This keeps PDFs out of
  `wwwroot` and out of the directory replaced on deploy.
- Development may omit it and falls back to `%LOCALAPPDATA%/SentinelMfgSuite/report-pdfs`.

## Layout

```
{RootPath}/reports/{reportId:N}/{revisionId:N}-{sha256}.pdf   finalized objects
{RootPath}/.tmp/{guid}.tmp                                   in-flight writes (never valid object keys)
```

Keys are strictly validated (`^reports/<32 hex>/<32 hex>-<64 lowercase hex>.pdf$`), resolved under the root, and
refused if any existing path component is a symbolic link, junction or reparse point. A revision can only read a key
carrying its own report id, revision id and recorded SHA-256.

## Filesystem permissions

- Run the application under a dedicated identity with read/write/delete-own access to `RootPath` only.
- Grant no access to other users or to the web server's static-file identity; do not place the root under any served
  directory, share or virtual directory.
- The root itself may be a mount point or link chosen by the operator; links *inside* it are refused.

## Persistence, backup and restore

- The root must live on persistent storage that survives deployments, restarts and instance replacement
  (not the application folder, container layer, or ephemeral disk).
- Back up the storage root together with the database, and restore them to a consistent point in time. A restore of
  the database without the matching objects makes reads fail explicitly with `ObjectMissing`.
- Objects are never regenerated automatically. If an object is lost, restore it from backup; the restored bytes must
  match the recorded SHA-256 and length to be readable.

## Atomicity and multi-instance limits

- Writes use a temp file, flush to disk, then `File.Move(overwrite: false)`. This is atomic on a local volume
  (NTFS, ext4, xfs). Network filesystems (SMB/NFS) may not provide the same non-overwriting, atomic move semantics and
  are **not supported** by this implementation.
- All application instances must see the same root. Multiple instances on separate local disks are unsupported:
  finalization on one instance would be unreadable from another. Use a single instance, or a shared local-semantics
  volume, until a cloud/object-store implementation of `IReportPdfStorage` exists.
- Finalization is safe under concurrency: the guarded database update lets exactly one request win; losers return
  `AlreadyFinalized` or `ConcurrencyConflict` and never delete or overwrite the winner's object.

## Orphans and manual reconciliation

A crash or database failure after a successful write but before the metadata update leaves an **orphan** object (valid
PDF, no referencing row). It is harmless: the key is content-addressed and revision-scoped, and an identical retry reuses
it. Nothing deletes orphans automatically.

Safe operator procedure:

1. List objects under `reports/` older than a safety window (for example 24 hours).
2. Exclude every key referenced by any `AssessmentReportRevisions.PdfStorageKey`.
3. Review the remainder (revision still Issued and unfinalized, or revision gone). Prefer retrying finalization for
   Issued revisions, which reuses the orphan.
4. Delete only reviewed candidates, using `IReportPdfStorage.DeleteAsync(key, expectedSha256)`, which refuses when the
   bytes do not match the expected hash.
5. Never delete an object referenced by a revision, including Superseded revisions.

Stale files in `.tmp/` older than the safety window may be removed by an operator; they are never treated as objects.

## Integrity

`ReadAsync` verifies key ownership, object existence, byte length and SHA-256 and returns explicit `ObjectMissing` or
`IntegrityFailure` results. A finalized PDF is never silently regenerated or replaced.
