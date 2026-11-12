# Visão geral

Como o LarisVMS se encaixa: o servidor web, os nós de gravação, os proxies de mídia e as câmeras.

## As peças

| Peça | O que faz |
|---|---|
| **Servidor web** | O aplicativo web do LarisVMS. Guarda a configuração no SQL Server ou no MariaDB, serve a interface e a API e retransmite o vídeo ao vivo e de reprodução, a menos que o streaming direto esteja ativado. Executa como o serviço do Windows *LarisVMS Web*. |
| **Nó de gravação** | Um serviço do Windows em cada máquina de gravação. Puxa o RTSP das suas câmeras, grava segmentos no seu caminho de armazenamento, aplica a retenção, executa a detecção de movimento e de IA e informa a saúde. Um servidor pode ter vários nós. |
| **Serviço de visão** | Processo filho opcional de um nó que executa a detecção de objetos por IA na GPU (ou CPU) do nó. Uma falha aqui nunca interrompe a gravação. |
| **Proxy de mídia** | Relay opcional entre navegadores e nós, para locais remotos ou links de baixa banda. |
| **Câmeras** | Câmeras ONVIF. Cada uma é atribuída a um nó de gravação. |

## Como o vídeo flui

1. Um nó abre uma sessão RTSP com cada uma das suas câmeras e grava com `ffmpeg -c copy`, então nenhuma recodificação acontece.
2. As gravações são escritas como arquivos de segmentos curtos (veja [Gravação](/Help/recording)) no caminho de armazenamento do nó.
3. A visualização ao vivo e a reprodução são servidas pelo nó e retransmitidas por este servidor, por um proxy de mídia, ou direto ao navegador (veja [Nós](/Help/nodes#direct-streaming)).

## Privacidade

O LarisVMS é auto-hospedado: seu vídeo e suas configurações ficam nas suas próprias máquinas, e nada sobre a sua instalação é coletado ou compartilhado. Não há telemetria nem chamadas para fora. A única requisição de saída de rotina é uma verificação diária das releases do LarisVMS no GitHub por uma nova versão (veja [Atualizações](/Help/installation#updates)); ela pode ser desativada. O outro download integrado é um modelo de IA YOLOX das releases do GitHub daquele projeto, na primeira vez que a detecção usa um, a menos que você mesmo coloque os arquivos no servidor. Os mapas de ruas da página [Mapa](/Help/live-and-playback#map) carregam suas imagens no seu navegador a partir do OpenStreetMap, ou de um servidor de tiles à sua escolha; as plantas não. Todo o resto só vai para as suas câmeras, os seus próprios servidores e os serviços que você configura, como e-mail e canais de alerta.

## Herança de configurações

A maioria das configurações existe em três níveis. A mais específica vence:

1. **Câmera**: na página de edição da própria câmera.
2. **Nó**: na página de edição do nó, em **Nós**.
3. **Global**: na página **Configurações** correspondente.

Um campo em branco (ou uma opção "herdar: …") usa o nível acima e mostra o valor que está herdando.

## Painel

O painel é feito de widgets. Cada usuário escolhe os seus, e o layout é salvo na conta dele, então o acompanha em outros navegadores.

- **Personalizar** (no canto superior direito; oculto quando a janela é estreita demais para a grade completa) destrava o layout. Arraste um widget pela barra de título, redimensione-o pelo canto ou remova-o com ×. **Adicionar widget** lista tudo que você pode adicionar, e **Redefinir layout** volta ao padrão. Clique em **Concluído** quando terminar.
- Durante a personalização, cada widget tem botões para movê-lo e redimensioná-lo sem arrastar.
- **Pausar atualizações** impede que os widgets mudem enquanto você os lê.
- A maioria dos widgets são listas dos 10 principais de câmeras ou nós, como armazenamento usado, taxa de quadros, largura de banda, gravações mais antigas, câmeras offline, tempo desde a última gravação e detecções. Alguns têm uma janela de tempo (1 h, 24 h, 7 d, 30 d), e alguns podem alternar entre os mais altos e os mais baixos primeiro.
- Clique no título de um widget para abrir seu relatório completo: toda câmera ou nó, 100 por página, com uma caixa de filtro, um filtro de nó, colunas ordenáveis e um download em CSV.
- **Atividade da câmera** mostra os últimos 7 dias de uma câmera como um mapa de calor por hora do dia; escolha a câmera na barra de título e clique no título para a página completa de [Atividade](/Help/live-and-playback#activity).
- **Últimas detecções** mostra as capturas mais recentes. O botão ⚙ escolhe quais tipos de detecção incluir; o movimento simples (nada identificado) fica desativado a menos que você o marque.
- **Tempo desde a última gravação** sinaliza uma câmera configurada para gravação Contínua que não grava há mais de 5 minutos.
- Os widgets de nós baseados em câmeras (contagem de câmeras, câmeras offline, taxa de quadros, tempo de armazenamento restante) omitem nós sem câmeras.
- Você só vê as câmeras às quais tem acesso. **Alertas recentes** só é oferecido a usuários que podem gerenciar regras de alerta.

Os widgets são atualizados a cada minuto.

## No celular

O site inteiro funciona no navegador de um celular. A barra lateral abre pelo botão ☰ no canto superior esquerdo.

Para instalá-lo como app, abra o site no Chrome no Android e escolha **Instalar app** no menu ⋮. No iOS, abra-o no Safari e escolha **Compartilhar → Adicionar à Tela de Início**. O app abre em tela cheia, sem a barra de endereço do navegador.

A instalação exige um certificado em que o celular confie. Os navegadores não instalam um site que usa o certificado autoassinado com o qual uma instalação nova começa; veja [Certificados](/Help/security#certificates).

## Por onde começar

- Instalação nova: [Instalação](/Help/installation)
- Adicionar câmeras: [Câmeras](/Help/cameras)
- Escolher o que manter: [Gravação](/Help/recording) e [Armazenamento e retenção](/Help/storage)
- Conectar suas próprias ferramentas: [API](/Help/api)
- Usar outro idioma: [Idiomas](/Help/languages)
- Teclado e leitores de tela: [Acessibilidade](/Help/accessibility)
- Algo errado: [Solução de problemas](/Help/troubleshooting)
