# Third-Party Notices

RedisMCPSharp is licensed under the MIT License (see `LICENSE`). It depends on
the third-party components listed below. Each remains under its own license;
this file is provided for attribution.

## NuGet packages

| Package | License |
| --- | --- |
| Microsoft.AspNetCore.App | MIT |
| Microsoft.Extensions.Hosting.WindowsServices | MIT |
| StackExchange.Redis | MIT |
| ModelContextProtocol.AspNetCore | Apache-2.0 |
| Serilog.AspNetCore | Apache-2.0 |
| Serilog.Enrichers.Environment | Apache-2.0 |
| Serilog.Enrichers.Process | Apache-2.0 |
| Serilog.Enrichers.Thread | Apache-2.0 |
| Serilog.Settings.Configuration | Apache-2.0 |
| Serilog.Sinks.Console | Apache-2.0 |
| Serilog.Sinks.File | Apache-2.0 |

The full text of the MIT and Apache-2.0 licenses is available at
<https://opensource.org/license/mit> and
<https://www.apache.org/licenses/LICENSE-2.0> respectively.

## Hand-rolled decoders

The BSON and Protobuf value decoders used by `get_deserialised` /
`detect_format` are hand-written in `Services/ValueDecoder.cs`. They exist
specifically to avoid taking a copyleft or restrictive dependency (e.g.
`MongoDB.Bson` or `Google.Protobuf`) and are covered by this project's MIT
license. They are intentionally inspection-grade only — not round-trip fidelity
serialisers.

## Trademarks

"Redis" is a trademark of Redis Ltd. Use of the name in this project does not
imply endorsement by, or affiliation with, Redis Ltd.
