# S3Harp

S3Harp is an S3-compatible object storage service written in C# on modern .NET. It speaks the Amazon S3 REST API, so existing S3 clients and SDKs can point at it instead of AWS.

## Status

**Pre-alpha.** The project is at the very beginning. This README describes intent, and will grow to record real capability as it lands.

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

## Naming conventions

The project name is styled differently depending on context — please keep these consistent:

| Form | Used for |
|---|---|
| `S3Harp` | NuGet package ID, .NET namespaces, README title, plain-text and code references |
| `s3harp` | Git repo, CLI binary, Docker/GHCR image, domain (`s3harp.dev`) |
| `S3HARP_` | Environment variable and config prefix |
| S3·harp | Marketing and branding, exclusively (logo, site header) |

## License

To be decided.
