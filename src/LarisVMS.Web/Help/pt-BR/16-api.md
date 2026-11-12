# API

Cada comando da API REST, com o que faz, o que precisa e um exemplo de uso.

Os exemplos usam valores inventados. Substitua `https://vms.example.com` pelo endereço do seu servidor, `lvk_EXAMPLEKEY` pela sua chave de API, e IDs como `00000000-0000-0000-0000-000000000001` por IDs reais.

## Primeiros passos

1. Em **Admin → Chaves de API**, selecione **Adicionar chave**. Escolha uma função e copie a chave quando ela for mostrada. Você só a vê uma vez.
2. Envie a chave em um cabeçalho `X-Api-Key` em toda requisição.
3. Chame `/api/v1/status` para verificar que a chave funciona e obter os IDs das suas câmeras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

Cada câmera na resposta tem um `cameraId`, que os outros comandos usam.

Somente caminhos que começam com `/api/v1` aceitam chaves de API. Os outros caminhos são internos à interface web e podem mudar sem aviso.

## Chaves e permissões

Você pode ter quantas chaves quiser, cada uma com suas próprias permissões. O acesso de uma chave vem de três coisas:

- **Função:** a chave tem as permissões da função à qual está vinculada.
- **Limites:** marque **Limitar esta chave** para permitir apenas algumas dessas permissões. Uma chave nunca pode fazer mais que a sua função.
- **Acesso às câmeras:** o acesso às câmeras da função em **Configurações → Acesso às câmeras** decide quais câmeras a chave pode usar. Por exemplo, a ação **Gravar** precisa estar ativada para uma câmera antes de uma chave poder gravá-la. Para limitar uma chave a certas câmeras, vincule-a a uma função com acesso apenas a essas câmeras.

Cada comando abaixo lista a permissão de que precisa. Uma chave limitada precisa ter essa permissão marcada.

Por exemplo, estas duas chaves poderiam compartilhar uma função de amplo acesso:

| Chave | Limites | Pode fazer |
|---|---|---|
| Monitoramento | Ver o painel de saúde das câmeras | Ler `/api/v1/status`, e nada mais |
| Botão da oficina | Ver câmeras, Iniciar e parar a gravação manual | Iniciar, parar e ler gravações |

Edite o nome e os limites de uma chave em **Admin → Chaves de API**. A chave em si não muda, então tudo que a usa continua funcionando. A função e a expiração de uma chave não podem mudar; crie uma nova chave. **Revogar** interrompe uma chave imediatamente.

Uma chave limitada nunca é tratada como administradora, mesmo que sua função seja **Super Admin**.

As ações de uma chave aparecem no log de auditoria como `API key: {name}`.

## Convenções

- As requisições e respostas são em JSON, exceto onde um comando devolve uma imagem ou um arquivo.
- Os horários são em UTC, no formato ISO 8601, como `2026-11-06T14:30:00Z`.
- Os nomes dos campos JSON estão em camelCase.
- **Configurações → Segurança** também se aplica aqui. A lista de permissão de gerenciamento cobre a API. Imagens e downloads contam como tráfego de visualização ao vivo e reprodução, então seguem aquela lista de permissão e, se você configurou uma, a porta separada.

| Status | Significado |
|---|---|
| `200` | Funcionou. |
| `400` | A requisição não é válida, como uma hora de término anterior à de início. |
| `401` | A chave está ausente, errada, revogada ou expirada. |
| `403` | A chave não tem a permissão, ou não tem acesso a uma câmera. |
| `404` | Esse item não existe. |
| `409` | A câmera não pode fazer isso agora, como gravar em uma câmera desativada, ou mover uma câmera PTZ usada por alguém com prioridade maior. |
| `429` | Requisições demais. Espere e tente de novo. Veja o cabeçalho `Retry-After`. |
| `502`, `503`, `504` | O nó de gravação não pôde ser alcançado ou não respondeu a tempo. |

## Monitoramento

### GET /health

Se o servidor está ativo. Não precisa de chave, para uso por balanceadores de carga e monitores de disponibilidade.

```bash
curl https://vms.example.com/health
```

```text
Healthy
```

### GET /api/v1/status

A saúde de cada câmera e o status de cada nó.

**Permissão:** Ver o painel de saúde das câmeras. Lista apenas as câmeras que a chave pode ver.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/status
```

```json
{
  "cameras": [
    {
      "cameraId": "00000000-0000-0000-0000-000000000001",
      "cameraName": "Workshop",
      "cameraEnabled": true,
      "nodeName": "Recorder-1",
      "nodeOnline": true,
      "fps": 15,
      "bitrateKbps": 4096,
      "healthFresh": true
    }
  ],
  "nodes": []
}
```

### GET /api/v1/cameras/motion-state

Os IDs das câmeras que veem movimento neste momento.

**Permissão:** Ver câmeras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/motion-state
```

```json
["00000000-0000-0000-0000-000000000001"]
```

### GET /api/v1/cameras/detection-state

Quais câmeras veem um objeto detectado neste momento, e o que ele é.

**Permissão:** Ver câmeras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/cameras/detection-state
```

```json
[
  {
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "detections": [
      { "kind": "Human", "label": "Person", "emoji": "🧍", "colorHex": "#ff9f1c" }
    ]
  }
]
```

## Gravação

Iniciar e parar a gravação manual, que mantém as gravações seja qual for o modo de gravação da câmera. Uma gravação dura até que algo a pare, inclusive após reinicializações e atualizações. Veja [Gravação](/Help/recording#manual-recording).

Iniciar uma câmera que já está gravando, ou parar uma que não está, é seguro e não muda nada.

Os comandos de início e parada retornam quais câmeras `changed`, quais `unchanged` e quais `skipped` por estarem desativadas ou não existirem.

### POST /api/v1/cameras/{cameraId}/recording/start

Inicia a gravação de uma câmera. Retorna `409` se a câmera estiver desativada ou não existir.

**Permissão:** Iniciar e parar a gravação manual, mais o acesso **Gravar** às câmeras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/start
```

```json
{
  "changed": ["00000000-0000-0000-0000-000000000001"],
  "unchanged": [],
  "skipped": []
}
```

### POST /api/v1/cameras/{cameraId}/recording/stop

Para a gravação de uma câmera.

**Permissão:** Iniciar e parar a gravação manual, mais o acesso **Gravar** às câmeras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/recording/stop
```

### POST /api/v1/recording/start

Inicia a gravação de várias câmeras. Retorna `403`, sem alterar nada, se a chave não tiver acesso a alguma delas.

**Permissão:** Iniciar e parar a gravação manual, mais o acesso **Gravar** às câmeras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/start
```

### POST /api/v1/recording/stop

Para a gravação de várias câmeras.

**Permissão:** Iniciar e parar a gravação manual, mais o acesso **Gravar** às câmeras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000002"]}' \
  https://vms.example.com/api/v1/recording/stop
```

### GET /api/v1/recording

Quais câmeras estão mantendo gravações neste momento. As câmeras em que nada acontece não são listadas.

`keeping` significa que as gravações estão sendo salvas no momento, por qualquer motivo. `manual` significa que uma gravação manual está ativa. Uma câmera offline com gravação manual ativa mostra `manual` sem `keeping`.

**Permissão:** Ver câmeras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/recording
```

```json
[
  {
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "keeping": true,
    "manual": true,
    "manualStartedUtc": "2026-11-06T14:30:00Z",
    "manualStartedBy": "API key: Workshop button"
  }
]
```

## Imagens

### GET /api/v1/cameras/{cameraId}/snapshot

Um JPEG tirado da câmera agora. Isso abre uma conexão curta com a câmera, então use a última miniatura abaixo se estiver verificando com frequência.

**Permissão:** Adicionar, editar e excluir câmeras, mais o acesso **Configurar** às câmeras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o snapshot.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/snapshot
```

### GET /api/v1/cameras/{cameraId}/thumbnail/latest

Uma imagem das gravações salvas mais recentes da câmera. Uma câmera sem gravações salvas não tem miniatura.

**Permissão:** Ver câmeras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o latest.jpg \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/thumbnail/latest
```

## PTZ

### POST /api/v1/cameras/{cameraId}/ptz/move

Começa a mover a câmera. Os valores vão de `-1` a `1`, onde `0` é sem movimento. A câmera continua se movendo até você chamar o stop.

| Campo | Significado |
|---|---|
| `panX` | Esquerda (negativo) ou direita (positivo). |
| `tiltY` | Para baixo (negativo) ou para cima (positivo). |
| `zoomX` | Afastar (negativo) ou aproximar (positivo). |

**Permissão:** Ver câmeras, mais o acesso **PTZ** às câmeras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"panX":0.5,"tiltY":0,"zoomX":0}' \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/move
```

### POST /api/v1/cameras/{cameraId}/ptz/stop

Para o movimento da câmera.

**Permissão:** Ver câmeras, mais o acesso **PTZ** às câmeras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/ptz/stop
```

## Dados de reprodução

### GET /api/v1/cameras/{cameraId}/timeline

Um resumo de um período, dividido em intervalos de tamanho igual: se cada um tem gravações ou movimento e as cores de quaisquer eventos.

| Parâmetro | Significado |
|---|---|
| `from`, `to` | O período. Obrigatório. |
| `buckets` | Quantos intervalos. Padrão 200. |

**Permissão:** Reproduzir gravações, mais o acesso **Reprodução** às câmeras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/timeline?from=2026-11-06T00:00:00Z&to=2026-11-07T00:00:00Z&buckets=24"
```

```json
[
  {
    "startUtc": "2026-11-06T00:00:00Z",
    "endUtc": "2026-11-06T01:00:00Z",
    "hasRecording": true,
    "hasMotion": false,
    "tagColorHex": "#e5383b",
    "tagColorHexes": ["#e5383b"]
  }
]
```

### GET /api/v1/cameras/{cameraId}/segments

Os arquivos de gravação salvos em um período.

| Parâmetro | Significado |
|---|---|
| `from`, `to` | O período. Obrigatório. |

**Permissão:** Reproduzir gravações, mais o acesso **Reprodução** às câmeras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/segments?from=2026-11-06T14:00:00Z&to=2026-11-06T15:00:00Z"
```

```json
[
  {
    "id": 1234,
    "startUtc": "2026-11-06T14:30:00Z",
    "endUtc": "2026-11-06T14:31:00Z",
    "isArchived": false
  }
]
```

### GET /api/v1/cameras/{cameraId}/activity

Quando os eventos costumam acontecer nesta câmera, por hora do dia.

| Parâmetro | Significado |
|---|---|
| `range` | Quanto tempo atrás, como `7d` (o padrão) ou `30d`. |
| `view` | `calendar` mostra por dia, para 30 dias ou mais. |
| `sel` | Um tipo de evento ou rótulo a incluir, como `Animal` ou `Animal:cat`. Repita para vários. |

**Permissão:** Reproduzir gravações, mais o acesso **Reprodução** às câmeras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/activity?range=30d"
```

## Marcadores

### GET /api/v1/cameras/{cameraId}/bookmarks

Os marcadores de uma câmera em um período.

| Parâmetro | Significado |
|---|---|
| `from`, `to` | O período. Obrigatório. |

**Permissão:** Reproduzir gravações, mais o acesso **Reprodução** às câmeras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/cameras/00000000-0000-0000-0000-000000000001/bookmarks?from=2026-11-06T00:00:00Z&to=2026-11-07T00:00:00Z"
```

```json
[
  {
    "id": "00000000-0000-0000-0000-00000000000b",
    "cameraId": "00000000-0000-0000-0000-000000000001",
    "cameraName": "Workshop",
    "timestampUtc": "2026-11-06T14:30:00Z",
    "note": "Print failed here",
    "createdByUserName": "API key: Workshop button",
    "createdAt": "2026-11-06T14:35:00Z"
  }
]
```

### POST /api/v1/bookmarks

Adiciona um marcador em um instante das gravações de uma câmera.

**Permissão:** Reproduzir gravações, e Criar marcadores nas gravações, mais o acesso **Reprodução** às câmeras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraId":"00000000-0000-0000-0000-000000000001","timestampUtc":"2026-11-06T14:30:00Z","note":"Print failed here"}' \
  https://vms.example.com/api/v1/bookmarks
```

```json
{ "id": "00000000-0000-0000-0000-00000000000b" }
```

## Exportações

Uma exportação salva as gravações de uma ou mais câmeras em um arquivo por câmera. As exportações rodam em segundo plano: crie uma, acompanhe e baixe cada arquivo quando terminar.

### POST /api/v1/exports

Inicia uma exportação. Limitada a 20 requisições por minuto por chave.

**Permissão:** Criar e baixar exportações, mais o acesso **Exportação** às câmeras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z"}' \
  https://vms.example.com/api/v1/exports
```

```json
{ "jobId": "00000000-0000-0000-0000-00000000000e" }
```

### GET /api/v1/exports

Lista as exportações e seu andamento. Cada exportação tem `items`, um para cada câmera. O `id` de um item é o que você usa para baixá-lo ou tentá-lo de novo.

**Permissão:** Criar e baixar exportações.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" https://vms.example.com/api/v1/exports
```

```json
[
  {
    "id": "00000000-0000-0000-0000-00000000000e",
    "createdUtc": "2026-11-06T14:40:00Z",
    "requestedByUserName": "API key: Workshop button",
    "fromUtc": "2026-11-06T14:00:00Z",
    "toUtc": "2026-11-06T14:30:00Z",
    "status": "Completed",
    "items": [
      {
        "id": "00000000-0000-0000-0000-00000000000f",
        "cameraId": "00000000-0000-0000-0000-000000000001",
        "cameraName": "Workshop",
        "nodeName": "Recorder-1",
        "status": "Completed",
        "errorMessage": null,
        "outputSizeBytes": 104857600,
        "redacted": false
      }
    ]
  }
]
```

### GET /api/v1/exports/items/{exportItemId}/download

Baixa um arquivo de exportação concluído.

**Permissão:** Criar e baixar exportações, mais o acesso **Exportação** às câmeras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" -o workshop.mp4 \
  https://vms.example.com/api/v1/exports/items/00000000-0000-0000-0000-00000000000f/download
```

### POST /api/v1/exports/{itemId}/retry

Tenta de novo um item de exportação que falhou.

**Permissão:** Criar e baixar exportações.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000f/retry
```

### DELETE /api/v1/exports/{jobId}

Exclui uma exportação e seus arquivos. Uma chave pode excluir as exportações que criou. Somente um administrador pode excluir as de outras pessoas.

**Permissão:** Criar e baixar exportações.

```bash
curl -X DELETE -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/exports/00000000-0000-0000-0000-00000000000e
```

## Bloqueios de provas

Um bloqueio de provas impede que as gravações sejam excluídas. Veja [Bloqueios de provas](/Help/live-and-playback#evidence-locks).

### GET /api/v1/evidence-locks

Lista os bloqueios.

| Parâmetro | Significado |
|---|---|
| `cameraId` | Somente esta câmera. Repita para várias. |
| `fromUtc`, `toUtc` | Somente bloqueios de gravações neste período. |
| `includeInactive` | `true` inclui os bloqueios que terminaram. |

**Permissão:** Reproduzir gravações, mais o acesso **Reprodução** às câmeras.

```bash
curl -H "X-Api-Key: lvk_EXAMPLEKEY" \
  "https://vms.example.com/api/v1/evidence-locks?cameraId=00000000-0000-0000-0000-000000000001"
```

### POST /api/v1/evidence-locks

Bloqueia gravações de uma ou mais câmeras.

| Campo | Significado |
|---|---|
| `cameraIds` | As câmeras. |
| `fromUtc`, `toUtc` | As gravações a bloquear. |
| `duration` | `1d`, `3d`, `7d`, `14d`, `30d`, `custom` ou `indefinite`. |
| `customUntilUtc` | Quando um bloqueio `custom` termina. |
| `reason` | Por que está bloqueado. |

**Permissão:** Reproduzir gravações, e Bloquear gravações para que a retenção não possa excluí-las, mais o acesso **Reprodução** às câmeras.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"cameraIds":["00000000-0000-0000-0000-000000000001"],"fromUtc":"2026-11-06T14:00:00Z","toUtc":"2026-11-06T14:30:00Z","duration":"30d","reason":"Incident 1042"}' \
  https://vms.example.com/api/v1/evidence-locks
```

### POST /api/v1/evidence-locks/{id}/remove

Remove um bloqueio. Um motivo é obrigatório.

**Permissão:** Reproduzir gravações, e as mesmas regras de remover um bloqueio na interface web.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" -H "Content-Type: application/json" \
  -d '{"reason":"Case closed"}' \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/remove
```

### POST /api/v1/evidence-locks/{id}/restore

Restaura um bloqueio removido durante o período de carência.

**Permissão:** Reproduzir gravações, e as mesmas regras de restaurar um bloqueio na interface web.

```bash
curl -X POST -H "X-Api-Key: lvk_EXAMPLEKEY" \
  https://vms.example.com/api/v1/evidence-locks/00000000-0000-0000-0000-00000000000c/restore
```

## Fora da API

Estes itens não estão disponíveis para chaves de API:

- Vídeo ao vivo. Use a interface web, ou as imagens acima.
- Reproduzir gravações diretamente. Use uma exportação.
- Configurações, usuários, funções, chaves de API, criptografia e backups.
- A configuração das câmeras, como zonas, agendamentos e configurações do dispositivo.
