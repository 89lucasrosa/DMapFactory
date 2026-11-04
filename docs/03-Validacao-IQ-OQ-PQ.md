# Protocolo de Validação IQ / OQ / PQ — DMapFactory v1.0

**Projeto:** DMapFactory — Diagnóstico de Sinais, Rastreio de Ciclo de Máquina e Histórico OPC UA  
**Plataforma:** Windows 10 / 11 (x64) — C# / .NET 10 (WPF)  
**Licença:** MIT (100% Open Source e Gratuito)  
**Referência:** `docs/02-Especificacao-Requisitos-v1.md` (`RNF-QUA-01..05`)

---

## 1. Objetivo e Escopo

Este documento estabelece o protocolo formal de validação **IQ (Installation Qualification)**, **OQ (Operational Qualification)** e **PQ (Performance Qualification)** do software **DMapFactory v1.0**, garantindo rastreabilidade direta aos requisitos funcionais (`RF-*`) e não funcionais (`RNF-*`) especificados no projeto.

---

## 2. Qualificação de Instalação (IQ — Installation Qualification)

A etapa de **IQ** verifica se o ambiente computacional, os binários da aplicação, as bibliotecas de terceiros e a estrutura de diretórios locais foram instalados corretamente.

### 2.1. Checklist de Ambiente e Instalação

| ID | Verificação | Critério de Aceite | Resultado Esperado | Status |
|---|---|---|---|---|
| **IQ-01** | Sistema Operacional | Windows 10 (21H2+) ou Windows 11 x64 | SO identificado como `Win32NT` x64 | [ ] OK |
| **IQ-02** | Runtime / Binários | `.NET 10.0 Desktop Runtime` ou pacote *self-contained* íntegro | `DMapFactory.exe` inicia sem erros de DLL | [ ] OK |
| **IQ-03** | Criação automática da loja PKI X.509 (`RNF-SEG-02`) | Ao iniciar pela primeira vez, gera certificado autoassinado de 2048 bits em `%LocalAppData%\DMapFactory\pki\own\certs` | Certificado `CN=DMapFactory OPC UA Client` válido criado | [ ] OK |
| **IQ-04** | Diretório de logs rotativos (`RNF-QUA-03`) | Criação automática de `%LocalAppData%\DMapFactory\logs\dmapfactory-YYYYMMDD.log` | Log de inicialização gravado no disco e visível no rodapé | [ ] OK |
| **IQ-05** | Diretório padrão de sessões (`RF-STO-01`) | Acesso de leitura/escrita em `Documentos\DMapFactory\Sessions` | Permissão de criação de `.csv` e `.session.json` confirmada | [ ] OK |
| **IQ-06** | Conformidade de licenças Open Source (Restrição Seção 5.5) | 100% dos pacotes NuGet sob licenças gratuitas (MIT, Apache-2.0, BSD) | Sem dependências comerciais ou pagas | [ ] OK |

---

## 3. Qualificação de Operação (OQ — Operational Qualification)

A etapa de **OQ** comprova que cada funcionalidade opera conforme especificado em condições normais e de contorno.

### 3.1. Execução Automatizada da Suíte OQ

Toda a base lógica e de integração OPC UA possui verificação automatizada via xUnit:

```powershell
dotnet test DMapFactory.sln
```

### 3.2. Matriz de Testes Operacionais (Manual e Automatizada)

| ID | Requisito(s) | Procedimento de Teste | Resultado Esperado | Cobertura Automatizada |
|---|---|---|---|---|
| **OQ-01** | `RF-CON-01..06`, `RNF-SEG-02/03` | Abrir *Nova Conexão*, informar `opc.tcp://localhost:48400/DMapFactory/TestServer`, clicar em **Descobrir endpoints** e **Testar conexão** nos modos `None` e `Basic256Sha256 (SignAndEncrypt)`. | Lista os endpoints do servidor, valida/auto-aceita o certificado em modo diagnóstico e retorna estado `Connected`. | `OpcUaDriverTests.Driver_DiscoversEndpoints_Connects_Browses_Reads_And_Subscribes` |
| **OQ-02** | `RF-TAG-01..05` | Na árvore *Navegador OPC UA*, expandir `Objects -> MachineSimulation` e clicar em **Monitorar todas as variáveis da pasta**. | Todas as variáveis (`Cycle_Active`, `Cycle_Count`, `Tag_001..Tag_050`) são adicionadas com `NodeId`, `Alias`, `DataType` e leitura inicial. | `OpcUaDriverTests` |
| **OQ-03** | `RF-ACQ-06/07`, `RF-STO-01..04` | Pressionar **F5 (Ficar on-line)** por 10 segundos e pressionar **F5 (Ficar off-line)**. | Cria par `<timestamp>_<projeto>.csv` + `.session.json`, grava snapshot inicial de todas as tags e encerra a sessão com `DroppedSamples = 0`. | `OpcUaDriverTests.SessionManager_EndToEnd_WithTestServer_RecordsCsvAndWideRows` |
| **OQ-04** | `RF-GRD-01..04`, `RF-GRD-10` | Observar a aba **Planilha** durante a aquisição on-line. | Formato largo (`#`, `Timestamp`, `Cycle`, + 1 coluna por tag). Mudanças no mesmo timestamp agrupadas na mesma linha (P3), células alteradas destacadas e linhas de início de ciclo (`Cycle_Active = 1`) realçadas. | `AcquisitionAndStorageTests.LiveSnapshot_GroupsSameTimestamp_And_DetectsMachineCycles` |
| **OQ-05** | `RF-CHT-01..04` | Alternar para a aba **Gráfico**, habilitar **Cursores A/B** e usar `Shift + Clique` em dois pontos do gráfico. | Até 8 penas exibidas simultaneamente, curvas analógicas em degrau (`StepHorizontal`), tags booleanas em faixas digitais `[D]` empilhadas e cálculo exato de $\Delta t$ entre os cursores A e B. | Verificação visual na UI WPF (`MainWindow`) |
| **OQ-06** | `RF-GRD-08` | Clicar em **Exportar** na barra de ferramentas e salvar nos formatos `.xlsx`, `.csv` (Largo), `.csv` (Estreito) e `.txt`. | Arquivos gerados sem erro; planilha `.xlsx` contém abas `Planilha` (com células alteradas destacadas em amarelo suave e ciclos em azul-acinzentado) e `Tags`. | `AcquisitionAndStorageTests.SpreadsheetExporter_ExportsWideCsv_NarrowCsv_And_Xlsx` |
| **OQ-07** | `RF-STO-06`, `RNF-SEG-01` | Salvar o projeto em `.dmproj` (`Arquivo -> Salvar projeto`), fechar e reabrir. | Conexões, grupos e tags restaurados integralmente; senhas salvas criptografadas via Windows DPAPI (`EncryptedPassword`). | `SecretProtectorTests` & `ProjectFileStore_SavesAndLoadsProjectConfig` |
| **OQ-08** | `RF-UI-01/02` | Alterar o seletor de idioma no canto superior direito entre **Português (Brasil)**, **English (US)** e **Español**. | Todos os menus, cabeçalhos, botões e barra de status mudam instantaneamente sem reiniciar a aplicação e sem perda de aquisição. | `LocalizationTests.AllSupportedLanguages_ReturnTranslatedStrings` |

---

## 4. Qualificação de Desempenho (PQ — Performance Qualification)

### 4.1. Critério de Aceite do MVP (`Q-QUA-03` / Seção 12)

> **Critério oficial de aceite:** Coletar **50 tags a 100 ms** (~500 amostras/segundo) contra o servidor OPC UA de simulação com **zero perda de amostras (`DroppedSamples == 0`)**, uso de buffer controlado ($< 80\%$), gravação íntegra em CSV+JSON e interface fluida em modo Planilha e Gráfico.

### 4.2. Procedimento de Execução do Ensaio de Carga (2 Horas)

1. Iniciar o servidor OPC UA de simulação com 50 tags a 100 ms:
   ```powershell
   dotnet run --project tools/DMapFactory.TestServer -- --port 48400 --tags 50 --interval 100
   ```
2. Iniciar o aplicativo **DMapFactory**:
   ```powershell
   dotnet run --project src/DMapFactory.App
   ```
3. Adicionar conexão para `opc.tcp://localhost:48400/DMapFactory/TestServer` e conectar.
4. Na árvore OPC UA, selecionar a pasta `MachineSimulation` e clicar em **Monitorar todas as variáveis da pasta** (52 variáveis: `Cycle_Active`, `Cycle_Count` e `Tag_001..Tag_050`).
5. Pressionar **F5 (Ficar on-line)** e manter a sessão ativa pelo período do ensaio (curto: 60 s; completo PQ: 2 horas).
6. Ao término, pressionar **F5 (Ficar off-line)** e inspecionar o arquivo `.session.json` gerado em `Documentos\DMapFactory\Sessions`:
   - Verificar `"DroppedSamples": 0`.
   - Verificar `"TotalSamples"` compatível com a taxa de geração ($\ge 500\text{ eventos/s} \times T_{\text{segundos}}$).
   - Abrir a sessão gravada (`Arquivo -> Abrir sessão gravada...`) e exportar uma janela para `.xlsx`.
