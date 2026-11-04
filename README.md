# DMapFactory

**DMapFactory** é uma ferramenta desktop open source (**MIT**) para Windows desenvolvida em **C# / .NET 10 (WPF, MVVM)** para diagnóstico de sinais de CLPs, rastreio de ciclo de máquina e historiamento de dados industriais via **OPC UA**, com visualização simultânea em **Modo Planilha (Formato Largo)** e **Modo Gráfico (Tendência + Faixas Digitais)**.

---

## Principais Recursos (v1.0)

- **Cliente OPC UA Completo (`DMapFactory.Drivers.OpcUa`)**:
  - Descoberta de endpoints (`GetEndpoints`), modos `None`, `Sign` e `SignAndEncrypt` (`Basic256Sha256`, `Aes128_Sha256_RsaOaep`, `Aes256_Sha256_RsaPss`).
  - Geração automática de certificado cliente X.509 autoassinado (2048 bits) e exportação `.der`.
  - Autenticação `Anonymous` e `UserName/Password` (com senha protegida em repouso via **Windows DPAPI**).
  - Reconexão automática (`SessionReconnectHandler`), navegação em árvore sob demanda (*lazy loading*), leitura de atributos e aquisição por **Subscription** (`MonitoredItem` com `QueueSize > 1`, `DiscardOldest = false` e `Deadband`) ou **Polling por mudança**.
- **Motor de Aquisição e Sessão CSV + JSON (`DMapFactory.Acquisition` & `DMapFactory.Storage`)**:
  - Transição explícita **On-line / Off-line (`F5`)** com leitura de snapshot inicial de todas as tags.
  - Buffer em memória de alta vazão (`System.Threading.Channels` para $\ge 50.000$ amostras) com zero perda em picos de disco.
  - Gravação automática por sessão em `<AAAA-MM-DD_HHMMSS>_<projeto>.csv` + `.session.json` (metadados, tags, ciclos e marcadores).
- **Modo Planilha em Formato Largo (`RF-GRD-01..10`)**:
  - Colunas `#`, `Timestamp (UTC)`, `Cycle` + **1 coluna dinâmica para cada tag monitorada**.
  - Nova linha a cada mudança de sinal (com agrupamento configurável por `SourceTimestamp`), destaque visual nas células que mudaram na linha e realce automático de ciclos de máquina (`Cycle_Active`).
  - Exportação para **Excel (`.xlsx`)**, **CSV Largo**, **CSV Estreito** e **TXT**.
- **Modo Gráfico (`RF-CHT-01..04`)**:
  - Até 8 penas simultâneas via `ScottPlot.WPF`, com interpolação em degrau (`StepHorizontal`), faixas digitais (`[D]`) empilhadas para sinais booleanos e **cursores duplos A/B** com cálculo instantâneo de $\Delta t$.
- **Interface & Internacionalização (`DMapFactory.Localization`)**:
  - Paleta cinza de baixo contraste para uso prolongado em campo e troca dinâmica de idioma em tempo real (**Português `pt-BR`**, **Inglês `en-US`** e **Espanhol `es`**).

---

## Estrutura da Solução

```text
DMapFactory/
├── docs/
│   ├── 01-Estruturacao-do-Projeto.md        # Levantamento arquitetural e questionário respondido
│   ├── 02-Especificacao-Requisitos-v1.md    # Especificação formal RF/RNF e arquitetura v1.0
│   └── 03-Validacao-IQ-OQ-PQ.md             # Protocolo de validação IQ/OQ/PQ e aceite MVP
├── src/
│   ├── DMapFactory.Core/                    # Modelos de domínio, contrato IDriver e DPAPI SecretProtector
│   ├── DMapFactory.Localization/            # Recursos pt-BR, en-US e es com troca em tempo real
│   ├── DMapFactory.Drivers.OpcUa/           # Driver OPC UA Client e gerenciador PKI X.509
│   ├── DMapFactory.Storage/                 # CsvSessionWriter/Reader, ProjectFileStore (.dmproj) e SpreadsheetExporter (.xlsx/.csv)
│   ├── DMapFactory.Acquisition/             # SessionManager (Channel buffer) e LiveSnapshot (planilha larga e ciclos)
│   └── DMapFactory.App/                     # Aplicação WPF (.NET 10)
├── tools/
│   └── DMapFactory.TestServer/              # Servidor OPC UA simulador (Cycle_Active + N tags @ M ms)
└── tests/
    ├── DMapFactory.Core.Tests/              # Testes unitários (Core, Localization, Storage, Acquisition, Export)
    └── DMapFactory.IntegrationTests/        # Testes integrados fim a fim contra o servidor OPC UA
```

---

## Como Compilar e Executar

### 1. Compilar e rodar todos os testes
```powershell
dotnet build DMapFactory.sln
dotnet test DMapFactory.sln
```

### 2. Iniciar o servidor OPC UA de simulação (50 tags a 100 ms)
```powershell
dotnet run --project tools/DMapFactory.TestServer -- --port 48400 --tags 50 --interval 100
```
Endpoint disponível em: `opc.tcp://localhost:48400/DMapFactory/TestServer`

### 3. Iniciar o DMapFactory
```powershell
dotnet run --project src/DMapFactory.App
```

---

## Licença

Distribuído sob a licença **MIT**. Consulte o arquivo [`LICENSE`](LICENSE) para mais detalhes.
