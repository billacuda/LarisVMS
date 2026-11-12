# Alertas, notificações e e-mail

Regras de alerta, notificações para usuários, canais da equipe e configuração do provedor de e-mail.

## Regras de alerta

**Configurações → Alertas** guarda as regras compartilhadas. Cada regra observa uma condição:

- **Câmera sem relatório**: a câmera parou de enviar relatórios de saúde.
- **Nó offline**: o nó parou de fazer check-in.
- **Armazenamento baixo**: o espaço livre caiu abaixo de uma porcentagem.
- **Failover de nó**: as câmeras de um nó passaram para o nó de reserva.
- **Movimento detectado**: movimento em uma câmera, ou em uma de suas zonas de movimento.
- **Objeto detectado**: a detecção de IA viu um objeto correspondente.
- **Velocidade acima de um limite**: um objeto cruzou um [controle de velocidade](/Help/ai-detection#speed-traps) a uma velocidade igual ou acima de um valor definido.
- **Bloqueio de provas prestes a terminar** e **Bloqueio de provas removido**: veja [bloqueios de provas](/Help/live-and-playback#evidence-locks).

As regras de saúde são verificadas uma vez por minuto. O **intervalo de espera** é o tempo mínimo entre alertas repetidos, para que uma condição que continua verdadeira não alerte a cada minuto.

### Alertas de detecção

**Objeto detectado** e **Velocidade acima de um limite** disparam em segundos após a detecção. Todo filtro é opcional, e todos os que você definir precisam corresponder:

- **Câmera**: uma câmera, as câmeras de um nó ou qualquer câmera.
- **Categoria** e **rótulo**: por exemplo Veículo, ou apenas caminhões.
- **Cor**: a cor do objeto, ou a cor da camisa ou da calça de uma pessoa. Veja [cores dos objetos](/Help/ai-detection#object-colors).
- **Zona**: apenas objetos parados dentro de uma das zonas da câmera (julgado pela parte inferior da caixa).
- **Ativo**: dias da semana e uma janela de horário, no fuso horário de quem salvou a regra. Uma janela como 22:00 a 06:00 atravessa a noite.

Uma detecção alerta uma vez. Em uma regra que observa várias câmeras, o intervalo de espera é contado separadamente para cada câmera, para que uma câmera movimentada não segure as outras.

### Alertas de movimento

**Movimento detectado** dispara em segundos após o movimento, vindo da detecção de movimento do servidor ou dos eventos de movimento da própria câmera. Escolha uma câmera, as câmeras de um nó ou qualquer câmera. Com uma câmera escolhida, **Zona** limita a uma das zonas de **Movimento no servidor** dessa câmera; deixe em **(câmera inteira)** para movimento em qualquer lugar. Os dias e horários de **Ativo** funcionam como nos alertas de detecção. Um trecho de movimento alerta uma vez, e o intervalo de espera segura o próximo.

### Canais da equipe

Cada regra também pode enviar para listas de e-mail, webhooks, ntfy, Pushover, Slack e Teams. Ative um canal e preencha seus campos.

## Notificações

As notificações vão para pessoas, no navegador delas ou por e-mail, em vez de para um canal da equipe. Cada pessoa escolhe o que quer em **Notificações** na barra lateral (também o 🔔 ao lado do seu nome). Uma notificação só chega a alguém para as câmeras que essa pessoa pode ver.

- **Notificações compartilhadas**: em uma regra de alerta, **Permitir que usuários se inscrevam** a lista em **Minhas notificações → Disponíveis**, onde qualquer um pode ativá-la. Os administradores também podem adicionar um usuário, ou todos de uma função, em **Inscritos** na regra.
- **Suas próprias notificações**: **Minhas notificações → Minhas → Nova notificação** cria uma só para você, com as mesmas escolhas de uma regra de alerta. Por exemplo, animais na câmera do quintal entre 22h e 6h. Só você a vê.
- **Silencie** uma notificação por uma hora, 8 horas, um dia ou até você reativá-la, sem cancelar a inscrição.

**Configurações → Notificações** liga ou desliga cada tipo de notificação para os usuários. Desligar um tipo não interrompe os canais da equipe nas regras de alerta.

Inscrever-se exige a permissão **Inscrever-se em notificações compartilhadas**, e criar as suas exige **Criar notificações pessoais**. Toda função tem as duas por padrão.

### Notificações do navegador

Ative-as em cada navegador em **Minhas notificações → Este navegador e dispositivos** e use **Enviar um teste**. **Configurações → Notificações** escolhe como elas são entregues:

| Entrega | Funciona quando | Precisa de |
|---|---|---|
| **Enquanto o LarisVMS está aberto** | Uma aba do LarisVMS ou o app instalado está aberto | Nada. Fica na sua rede. |
| **Web Push** | Nenhuma página do LarisVMS aberta (Chrome, Edge, Firefox e Safari). Em um computador, o próprio navegador precisa estar em execução; veja abaixo | **Gerar chaves de Web Push** uma vez, e acesso de saída à internet do servidor para os serviços de push dos fabricantes de navegadores. Sem conta. |
| **Firebase Cloud Messaging** | Nenhuma página do LarisVMS aberta, com o mesmo limite em computadores | Seu próprio projeto Firebase: a configuração do app web, o certificado de web push e uma chave de conta de serviço. Usado só quando o Web Push está desativado. |

Sem permissão para mostrar notificações, elas aparecem dentro das páginas abertas do LarisVMS. Em um celular, instale primeiro o LarisVMS como app (veja [a visão geral](/Help/overview)).

Os celulares recebem notificações push mesmo com o navegador fechado. Em um computador, um navegador só as recebe enquanto está em execução, e fechar sua última janela geralmente o encerra; o que for enviado nesse meio-tempo aparece quando ele iniciar de novo. Para mantê-lo em execução em segundo plano, ative **Continuar executando aplicativos em segundo plano quando o Google Chrome for fechado** (Chrome: Configurações → Sistema) ou **Continuar executando extensões e aplicativos em segundo plano quando o Microsoft Edge for fechado** (Edge: Configurações → Sistema e desempenho). Use o e-mail para quem precisa de alertas sem nenhum navegador em execução.

### Notificações por e-mail

As pessoas também podem escolher e-mail para uma notificação. Ela vai para o endereço da conta delas, pelo e-mail configurado abaixo. **Configurações → Notificações** pode desligar as notificações por e-mail.

## E-mail

Os alertas e notificações por e-mail são enviados pelo provedor configurado em **Configurações → E-mail**. **Salvar e enviar teste** verifica as configurações salvas.

- **SMTP**: host, porta, SSL/TLS e credenciais.
- **Microsoft Graph**: registre um app no Entra ID, conceda a ele a permissão de **aplicativo** `Mail.Send` (não delegada) com consentimento do administrador, e informe o ID de locatário, o ID do cliente e o segredo. Não há login por usuário.
- **Gmail**: crie um cliente OAuth do tipo *Aplicativo da Web* no Google Cloud. Adicione `https://<seu-host>/Admin/OAuthCallback` como URI de redirecionamento autorizado e ative o escopo `https://mail.google.com/`. Depois use **Salvar e conectar ao Google** uma vez para conceder o consentimento.

Os segredos salvos são criptografados. Se as chaves de proteção de dados do servidor forem perdidas, informe-os de novo e reconecte o Gmail.
