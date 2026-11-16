# Home Assistant

Mostre suas câmeras, detecções e nós de gravação no Home Assistant, e inicie gravações ou adicione marcadores por ele.

> **Experimental.** O suporte ao Home Assistant é novo, e as pessoas usam o Home Assistant para muitas coisas diferentes. [Conte como funciona para você](https://github.com/billacuda/LarisVMS/discussions/new?category=general&title=Home%20Assistant%20feedback): para que você o usa, o que funciona bem e o que dá errado.

## Como funciona

O LarisVMS envia ao broker MQTT que o Home Assistant já usa. A descoberta MQTT do Home Assistant então adiciona um dispositivo para cada câmera e cada nó de gravação, sem escrever YAML.

O Home Assistant atua com uma função que você escolhe, do mesmo jeito que uma chave de API:

- Ele só recebe as câmeras que essa função pode ver em **Configurações → Acesso às câmeras**.
- Ele só pode fazer o que essa função permite. Por exemplo, o interruptor de gravação só aparece quando a função tem **Iniciar e parar a gravação manual** e o acesso **Gravar** às câmeras.
- O que ele faz aparece no log de auditoria como **Home Assistant**.

## Configuração

1. No Home Assistant, instale o complemento **Mosquitto broker** e a integração **MQTT**, se ainda não usa. Crie um login do broker para o LarisVMS, como um usuário do Home Assistant chamado `larisvms`.
2. No LarisVMS, crie uma função para o Home Assistant em **Configurações → Permissões → Funções**. Dê a ela **Ver câmeras**, e acesso às câmeras que quiser em **Configurações → Acesso às câmeras**. Adicione **Iniciar e parar a gravação manual**, ou **Reproduzir gravações** e **Criar marcadores nas gravações**, se o Home Assistant tiver que gravar ou adicionar marcadores.
3. Abra **Configurações → Home Assistant** e ative. Informe o endereço do broker (o do seu Home Assistant, se usar o complemento), a porta (geralmente `1883`), o login e a função. Selecione **Testar conexão** e depois **Salvar**.
4. No Home Assistant, abra **Configurações → Dispositivos e serviços → MQTT**. Suas câmeras aparecem lá como dispositivos.

O status em **Configurações → Home Assistant** mostra se o LarisVMS está conectado e quantas câmeras está enviando. Se o broker reiniciar ou a rede cair, o LarisVMS se reconecta sozinho. Quando o Home Assistant reinicia, o LarisVMS envia tudo de novo.

## O que aparece no Home Assistant

Cada câmera recebe:

| Entidade | Mostra |
|---|---|
| **Movimento** | Ligado enquanto houver movimento ou uma detecção |
| **Pessoa**, **Veículo**, **Animal** | Ligado enquanto esse tipo de objeto for detectado |
| **On-line** | Se a câmera está enviando vídeo |
| **Detecção mais recente** | Uma imagem do último objeto detectado |
| **Última detecção**, **Último evento** | O que foi detectado por último, e quando |
| **Última velocidade** | A última velocidade medida pelos [radares de velocidade](/Help/ai-detection), nas unidades de **Configurações → Detecção de IA** |
| **Gravação manual** | Um interruptor que inicia e para uma gravação manual, quando a função permite |
| **Adicionar marcador** | Um botão que adiciona um marcador no horário atual, quando a função permite |

Cada nó de gravação recebe **On-line** e **Armazenamento livre**.

Um sensor liga um ou dois segundos depois de uma detecção, e desliga uns 25 segundos depois que ela termina. É o mesmo que o indicador de movimento em **Ao vivo**.

## Vídeo ao vivo

A **Generic Camera** do Home Assistant pode mostrar o vídeo ao vivo do LarisVMS. Ela usa o mesmo stream das páginas web, então a câmera não precisa enviar outro.

1. Dê à função do Home Assistant a permissão **Transmitir vídeo ao vivo para outros programas**. Nenhuma função a tem até você concedê-la.
2. Em **Admin → Chaves de API**, adicione uma chave vinculada a essa função.
3. No Home Assistant, adicione uma **Generic Camera** para cada câmera. **Configurações → Home Assistant** lista a **URL da imagem estática** e a **Fonte do stream** de cada câmera. Escolha a autenticação **Basic**, informe `apikey` como usuário e a chave como senha.

A [API](/Help/api) descreve o stream.

## Alertas por webhook

O webhook de uma regra de alerta também pode enviar ao Home Assistant. No Home Assistant, crie uma automação com um gatilho **Webhook** e coloque o endereço dela no webhook da regra, como `http://homeassistant.local:8123/api/webhook/larisvms-front-door`.

O webhook envia JSON com a regra, a mensagem e a hora e, quando o alerta os tem, o evento, a câmera, o nó, o objeto detectado, a velocidade e `snapshotPath`. Junte `snapshotPath` ao endereço do seu LarisVMS para obter a imagem da detecção com uma chave de API.

```yaml
triggers:
  - trigger: webhook
    webhook_id: larisvms-front-door
    local_only: true
actions:
  - action: notify.notify
    data:
      message: "{{ trigger.json.message }}"
```

## Se algo estiver faltando

- **Não conectado:** confira o endereço, a porta e o login com **Testar conexão**. Um broker que exige TLS geralmente usa a porta `8883`.
- **Nenhum dispositivo no Home Assistant:** confira se a integração MQTT está configurada e se o prefixo de descoberta é igual ao do Home Assistant (`homeassistant`, a menos que você tenha mudado).
- **Falta uma câmera:** a função não pode vê-la, ou a câmera está desativada.
- **Sem interruptor de gravação ou botão de marcador:** falta à função a permissão ou o acesso à câmera para isso.
- **O webhook não chega:** o LarisVMS não envia webhooks para `localhost`. Use o endereço de rede do Home Assistant, mesmo que ele rode na mesma máquina.
