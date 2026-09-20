# S3Harp

S3Harp is an S3-compatible object storage service written in C# on modern .NET. It speaks the Amazon S3 REST API, so existing S3 clients and SDKs can point at it instead of AWS.

## Status

**Pre-alpha.** Early, but real: the AWS SDK talks to S3Harp today.

Working now, each proven by integration tests driving the real AWS SDK:

- AWS Signature Version 4 on every request — header verification plus chunked payload signing, including the trailer variants current SDKs send by default; the request time comes from `x-amz-date` or, when absent, the `Date` header
- Bucket create, list, head, and delete
- Object PUT, GET, HEAD, and DELETE with ETags, content types, the standard content headers (`Cache-Control`, `Content-Disposition`, `Content-Encoding`, `Content-Language`, `Expires`), and `x-amz-meta-*` metadata, stored durably (temp file → fsync → rename)
- Range GETs (`206 Partial Content`), so parallel ranged downloads — the AWS CLI's default for large files — reassemble exactly
- Conditional GET and HEAD (`If-Match`, `If-None-Match`, `If-Modified-Since`, `If-Unmodified-Since`) answering `304` or `412`, and the matching `x-amz-copy-source-if-*` conditions on CopyObject
- Conditional writes: `If-None-Match: *` creates only when the key is free, and `If-Match` overwrites only the ETag it names, on PutObject and CompleteMultipartUpload alike, checked atomically in the metadata index
- Retrying CompleteMultipartUpload with the same parts after it already succeeded returns the same result
- Batch delete (`DeleteObjects`), up to 1000 keys per request with quiet-mode support
- Presigned URLs — SDK-generated time-limited GET/PUT links work from any plain HTTP client, with expiry enforced
- ListObjectsV2 with prefixes, delimiter grouping into common prefixes, `start-after`, and continuation-token pagination, plus the original marker-based ListObjects for clients that still use it
- ListObjectVersions, reporting every object as its single current `null` version, so tooling written for versioned buckets can enumerate and clean up S3Harp buckets
- Multipart uploads — initiate, upload parts, list parts and in-progress uploads, complete, abort — with S3's multipart ETag format and its 5 MiB minimum for every part but the last (`EntityTooSmall`); part assembly goes through `copy_file_range`, so reflink-capable filesystems share blocks instead of rewriting them
- Server-side CopyObject, with metadata copied or replaced per the metadata directive
- S3 XML error responses that SDKs parse into their typed exceptions

## Conformance

Ceph's [s3-tests](https://github.com/ceph/s3-tests) suite runs against every push in CI. `tests/conformance/must-pass.txt` lists the cases S3Harp passes; the run fails if any listed case fails, and also fails if a case outside the list starts passing, so the list only ever grows. To run it locally (needs `uv`):

```sh
dotnet build src/S3Harp.Server --configuration Release
tests/conformance/run.sh
```

## Formatting

CSharpier formats the C# and the project files; Prettier formats the Markdown, YAML and JSON. CI rejects a push that either would change, so format before pushing (needs `npm`):

```sh
dotnet tool restore && dotnet csharpier format .
npm ci && npx prettier --write .
```

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

Every setting has one name, such as `data_dir`. That name is the key in a settings file, the suffix of the `S3HARP_DATA_DIR` environment variable and the `--data-dir` flag; `s3harp --help` lists them all. A later source overrides an earlier one: the settings file, then the environment, then the flags. The server authenticates every request with AWS Signature Version 4 against its root keypair, which it requires at startup. A setting that is missing or invalid stops the server with a message naming it.

| Setting             | Purpose                                                                                     |
| ------------------- | ------------------------------------------------------------------------------------------- |
| `access_key_id`     | Required. The access key id clients sign requests with                                      |
| `secret_access_key` | Required. The matching secret key                                                           |
| `data_dir`          | Required. The directory holding all stored data, including the metadata index               |
| `bind`              | The address to listen on; defaults to `127.0.0.1`, so set `0.0.0.0` to serve other machines |
| `port`              | The port to listen on; defaults to `9000`, the port local S3 tooling expects                |
| `domain`            | The domain buckets are addressed under in virtual-hosted style; defaults to `localhost`     |
| `config`            | A settings file to read, in the format its extension names: `.toml` or `.json`              |

A settings file is read only when `config` names it, and naming a file that does not exist or cannot be parsed stops the server. JSON files may contain comments.

```toml
# The settings s3harp starts with; S3HARP_ variables and flags override these.
access_key_id = "S3HARPEXAMPLEKEY"
secret_access_key = "example-secret"
data_dir = "/var/lib/s3harp"
bind = "0.0.0.0"
```

```sh
s3harp --config /etc/s3harp/s3harp.toml
```

Buckets are reachable in both of S3's addressing styles: path style (`http://localhost:9000/my-bucket/key`) and virtual-hosted style (`http://my-bucket.localhost:9000/key`), which SDKs use unless told otherwise. Every `*.localhost` name resolves to the loopback address, so the default domain works without DNS setup; set `domain` when serving under another name.

## Naming conventions

The project name is styled differently depending on context — please keep these consistent:

| Form      | Used for                                                                        |
| --------- | ------------------------------------------------------------------------------- |
| `S3Harp`  | NuGet package ID, .NET namespaces, README title, plain-text and code references |
| `s3harp`  | Git repo, CLI binary, Docker/GHCR image, domain (`s3harp.dev`)                  |
| `S3HARP_` | Environment variable and config prefix                                          |
| S3·harp   | Marketing and branding, exclusively (logo, site header)                         |

## License

S3Harp is licensed under the [Apache License 2.0](LICENSE).
