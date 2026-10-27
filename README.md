# Opsifin Print Agent

Windows service (.NET 10) yang mencetak invoice dari Opsifin Print Hub ke printer di PC klien.

- PRD: [`docs/PRD.md`](docs/PRD.md) · ringkasan keputusan: [`docs/PRD-ringkasan-keputusan.md`](docs/PRD-ringkasan-keputusan.md)
- Kontrak API (salinan dari repo Hub): [`contracts/agent-v1.yaml`](contracts/agent-v1.yaml)
- Hub: repo `opsifin-print-hub`

```bash
dotnet build
dotnet test
dotnet run --project src/Agent.Service
dotnet run --project src/Agent.Service -- --list-printers   # spike B2, lihat docs/spike-b2.md
```

Menjalankan dan mencetak butuh Windows 10/11 dengan printer nyata. Panduan lengkap ada di [`CLAUDE.md`](CLAUDE.md).
