# S3Harp

S3Harp is an S3-compatible object storage service written in C# on modern .NET. It speaks the Amazon S3 REST API, so existing S3 clients and SDKs can point at it instead of AWS.

## Status

**Pre-alpha.** Early, but real: the AWS SDK talks to S3Harp today.

Working now, each proven by integration tests driving the real AWS SDK:

- AWS Signature Version 4 on every request — header verification plus chunked payload signing, including the trailer variants current SDKs send by default
- Bucket create, list, head, and delete
- Object PUT, GET, HEAD, and DELETE with ETags, content types, and `x-amz-meta-*` metadata, stored durably (temp file → fsync → rename)
- Range GETs (`206 Partial Content`), so parallel ranged downloads — the AWS CLI's default for large files — reassemble exactly
- Batch delete (`DeleteObjects`), up to 1000 keys per request with quiet-mode support
- Presigned URLs — SDK-generated time-limited GET/PUT links work from any plain HTTP client, with expiry enforced
- ListObjectsV2 with prefixes, delimiter grouping into common prefixes, `start-after`, and continuation-token pagination
- Multipart uploads — initiate, upload parts, list parts and in-progress uploads, complete, abort — with S3's multipart ETag format; part assembly goes through `copy_file_range`, so reflink-capable filesystems share blocks instead of rewriting them
- Server-side CopyObject, with metadata copied or replaced per the metadata directive
- S3 XML error responses that SDKs parse into their typed exceptions

## What it's for

Two uses, in a tension that keeps the project honest:

- **A local S3 stand-in for development and testing** — spin it up, run your code against it, throw it away.
- **Real self-hosted storage** — something you can trust with actual data on your own hardware, which makes correctness and durability requirements.

## Scope

**Core S3 essentials first.** Buckets, object PUT/GET/DELETE, listing, and multipart uploads — enough that mainstream S3 SDK usage works. The wider API surface (versioning, lifecycle rules, ACLs) becomes candidate work once the core is solid.

**AWS Signature Version 4 from the start.** SDKs sign every request, and auth cuts deep into request parsing — it's foundational.

## Storage design

S3Harp itself is the storage backend: objects are stored as plain files on the local filesystem. Where the filesystem supports copy-on-write reflinks (btrfs, XFS, ZFS block cloning), S3Harp aims to exploit them — object copies and multipart assembly can share blocks instead of rewriting bytes. Everywhere else, the same operations use ordinary copies: S3Harp runs on any filesystem, and CoW simply makes it faster where available.

## Configuration

Configuration uses the `S3HARP_` prefix for environment variables. The server authenticates every request with AWS Signature Version 4 against its root keypair, which it requires at startup:

| Variable | Purpose |
|---|---|
| `S3HARP_ACCESS_KEY_ID` | The access key id clients sign requests with |
| `S3HARP_SECRET_ACCESS_KEY` | The matching secret key |
| `S3HARP_DATA_DIR` | The directory holding all stored data, including the metadata index |

## Naming conventions

The project name is styled differently depending on context — please keep these consistent:

| Form | Used for |
|---|---|
| `S3Harp` | NuGet package ID, .NET namespaces, README title, plain-text and code references |
| `s3harp` | Git repo, CLI binary, Docker/GHCR image, domain (`s3harp.dev`) |
| `S3HARP_` | Environment variable and config prefix |
| S3·harp | Marketing and branding, exclusively (logo, site header) |

## License

S3Harp is licensed under the [Apache License 2.0](LICENSE).
