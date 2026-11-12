# Visualização ao vivo e reprodução

Visualizações, blocos ao vivo, a linha do tempo de reprodução, exportações, marcadores, bloqueios de provas, capturas, atividade e o mapa.

## Visualizações

Uma **visualização** é um layout de câmeras salvo. Crie visualizações em **Visualizações** arrastando e redimensionando blocos. **Ao vivo** e **Reprodução** mostram uma visualização. Em celulares, o layout colapsa para uma ou duas colunas. As visualizações podem rodar em tela cheia no modo quiosque, ou alternar entre várias visualizações por um temporizador. Uma visualização compartilhada pode ser usada por todos que veem visualizações, mas apenas o proprietário ou um administrador pode alterá-la ou excluí-la.

## Blocos ao vivo

- Dê um clique duplo em um bloco para tela cheia. Em tela cheia, role ou faça pinça para aproximar e arraste para mover.
- Os controles de um bloco aparecem quando você passa o mouse sobre ele, ou quando chega a eles com Tab. Veja [Acessibilidade](/Help/accessibility).
- Cada bloco com áudio tem seus próprios controles de silenciar e volume. Os blocos sempre começam silenciados.
- O **Streaming adaptativo** (em **Configurações → Visualização ao vivo**) mostra aos blocos pequenos o stream Secundário e passa ao Principal quando o bloco é grande ou está em tela cheia. Cada bloco também tem uma escolha manual Auto/HD/SD.
- Os selos mostram movimento e objetos detectados. **Configurações → Eventos** define suas cores e o canto.
- As chaves de IA da barra de ferramentas desenham uma caixa em volta de cada objeto detectado: **Em movimento**, **Parado**, ou ambos. **Confiança** adiciona a pontuação de cada caixa, e **Velocidade** e **Cor** (ativadas por padrão) adicionam sua velocidade, depois que um controle de velocidade a cronometra, e sua cor. **Trajeto** (com Em movimento ativado) desenha a linha percorrida por cada objeto em movimento a partir do centro da sua caixa, na cor da caixa. A linha permanece enquanto o objeto continua se movendo. Depois que ele para ou sai, a linha permanece pelo tempo de **Persistência do trajeto** (**Configurações → Visualização ao vivo**, 5 segundos por padrão) e depois se dissolve a partir do início durante o tempo de **Dissolução do trajeto** (mesma página, 3 segundos por padrão).
- As câmeras PTZ têm um direcional e zoom.
- 🔴 no canto inferior esquerdo significa que as gravações da câmera estão sendo mantidas agora, por qualquer motivo. As câmeras contínuas o mostram o tempo todo. Pode levar até 20 segundos para mudar depois de movimento ou de um evento.
- Ⓜ️ ao lado significa que uma [gravação manual](/Help/recording#manual-recording) está ativa. Ⓜ️ sozinho significa que a gravação está ativa, mas a câmera está offline e nada está sendo salvo.
- O botão ⏺ inicia uma gravação manual e ⏹ a para. Em uma visualização com outras câmeras, ele pergunta se você quer dizer esta câmera ou todas. Quem tem permissão pode parar uma gravação, seja quem for que a iniciou.

## Falar por uma câmera

As câmeras com alto-falante mostram um botão 🎙️ no bloco ao vivo. Segure-o (ou dê foco nele e segure Espaço) para falar e solte para parar. **▾** ao lado escolhe o microfone: um fone, uma webcam ou qualquer outra entrada que o navegador possa usar.

- Seu navegador pede acesso ao microfone na primeira vez. Ele só permite isso quando o site é aberto por **HTTPS**; veja [Certificados](/Help/security#certificates).
- Só uma pessoa por vez pode falar por uma câmera.
- Exige a permissão **Falar** na câmera (**Configurações → Acesso às câmeras**). A câmera deve suportar áudio bidirecional ONVIF (uma saída de áudio no seu perfil ONVIF). As câmeras Dahua, Amcrest, Lorex e EmpireTech cujo firmware não oferece isso recorrem à API de áudio própria da câmera.
- Tudo o que é dito é gravado. O microfone da própria câmera faz parte da gravação de vídeo como de costume, e cada pressão do botão é salva junto das gravações. As pressões aparecem na linha do tempo da reprodução em verde-azulado e tocam sincronizadas com o vídeo, usando o volume do bloco. As exportações as incluem, misturadas ao áudio da própria câmera. Elas são mantidas pelo mesmo tempo que as gravações da câmera.
- Cada sessão é escrita no log de auditoria.

## Linha do tempo

A reprodução tem duas linhas do tempo: uma para a câmera selecionada e uma combinada de todas as câmeras. Ambas são coloridas por movimento, detecções, tags de evento e áudio bidirecional.

- Role para aproximar (de semanas até segundos), arraste para mover e clique para levar todos os blocos àquele instante.
- Clique na hora acima da linha do tempo para digitar uma hora exata.
- As setas avançam um segundo enquanto pausado; Shift+seta avança dez.
- Velocidades de até 8× tocam com fluidez. Velocidades maiores percorrem as gravações como uma apresentação de slides.
- Passe o mouse sobre a linha do tempo para ver uma miniatura de prévia.
- Os trechos com movimento ou evento são listrados, para não depender só da cor.
- Os botões − e + ao lado da hora também dão zoom, e a linha do tempo funciona pelo teclado. Veja [Acessibilidade](/Help/accessibility).

Sua posição e seu zoom são lembrados entre as visitas.

## Exportações

**Exportar…** na Reprodução cria um arquivo de vídeo para cada câmera selecionada em um período. As exportações rodam em segundo plano e aparecem na página **Exportações**, onde podem ser baixadas, tentadas novamente ou excluídas. Uma exportação cobre até 64 câmeras, 31 dias e 1.000 câmeras-hora no total (4 câmeras por uma semana são 672); para mais, faça várias. As exportações concluídas são mantidas por 7 dias e depois removidas junto com seus arquivos.

- **✏ Ocultar** ao lado de uma câmera oculta áreas em preto apenas na exportação daquela câmera, como a janela de um vizinho ou um passante. Desenhe retângulos (arrastando) ou polígonos (clique em cada canto; clique no primeiro ponto, dê um clique duplo ou pressione Enter para concluir; o clique direito remove o último ponto) sobre um quadro da hora de início da exportação. As gravações em si não mudam.
- Uma exportação com ocultação é recodificada, então leva mais tempo que uma simples. É marcada como **Ocultada** na página Exportações, e o log de auditoria registra que áreas foram ocultadas.
- As [máscaras de privacidade](/Help/motion) de uma câmera já fazem parte das gravações dela, então toda exportação as inclui.
- Qualquer áudio bidirecional falado pela câmera durante o período é misturado ao áudio da exportação.

### Verificar uma exportação

Cada segmento gravado recebe uma impressão digital (SHA-256) conforme é escrito, e os segmentos de cada câmera são encadeados: cada elo cobre a impressão digital do seu segmento e o elo anterior, então alterar, remover ou reordenar gravações quebra a cadeia. Uma vez por hora o servidor assina até onde chegou a cadeia de cada câmera.

Cada exportação concluída também recebe um **🔏 Manifesto** assinado na página Exportações. Ele registra a impressão digital do arquivo exportado, as gravações de origem e seu lugar na cadeia, e se cada arquivo de origem ainda correspondia à sua impressão digital quando foi exportado.

Para conferir uma exportação, abra **Exportações → Verificar uma exportação** e escolha o manifesto e o vídeo. A verificação roda no seu navegador e nada é enviado. Ela confirma que:

- o manifesto foi assinado por este servidor e não foi editado;
- o vídeo é exatamente o arquivo exportado;
- as gravações de origem não tinham mudado desde que foram feitas e se ligam a um checkpoint assinado.

Mantenha o manifesto junto com o vídeo ao repassar uma exportação. Qualquer pessoa pode conferir a assinatura sem o LarisVMS: salve o `publicKeyPem` do manifesto em `key.pem`, o texto do seu `signedPayload` em `payload.txt` e o seu `signatureDer` decodificado de base64 em `sig.der`, e depois execute `openssl dgst -sha256 -verify key.pem -signature sig.der payload.txt`. As gravações feitas antes deste recurso não têm impressões digitais, então só a própria exportação pode ser conferida.

A assinatura está sempre ativada. A chave de assinatura do servidor, com um download da sua chave pública, fica em **Configurações → Criptografia e assinatura**.

## Marcadores

Marque um instante durante a Reprodução com uma nota. **Marcadores** lista todos, com um link de volta ao instante exato. Os marcadores são compartilhados com todos que podem ver a reprodução e expiram quando suas gravações vencem.

## Bloqueios de provas

Um bloqueio de provas impede que gravações sejam excluídas pela retenção. Na Reprodução, clique em **🔒 Bloquear…**, escolha as câmeras e o início e o fim, escolha por quanto tempo manter (1, 3, 7, 14 ou 30 dias, até uma data, ou indefinidamente) e dê um motivo. Os trechos bloqueados aparecem como uma faixa violeta no alto da linha do tempo. Um bloqueio cobre até 7 dias de gravações.

- **As gravações bloqueadas nunca são excluídas**, nem pela retenção, nem por cotas, nem pela expiração do arquivo morto, nem mesmo quando o disco está cheio. Se um gravador enche de gravações bloqueadas, a entrada dele em Nós mostra **cheio de gravações bloqueadas**, e a gravação para quando o disco enche. Encerre ou remova bloqueios, exporte as gravações ou adicione armazenamento.
- **Bloqueios longos**: qualquer coisa acima de 30 dias mostra um aviso. Quando o armazenamento está apertado, é melhor exportar o vídeo (**Exportar este período em vez disso**) e guardá-lo em outro lugar.
- **Removendo um bloqueio**: **Bloqueios de provas** lista cada bloqueio com seu tamanho e expiração. Quem colocou um bloqueio, ou um Super Admin, pode removê-lo com um motivo. As gravações continuam protegidas por mais **24 horas** caso a remoção tenha sido um engano, e **Desfazer remoção** restaura o bloqueio até lá.
- **Registrado**: colocar, remover, restaurar e expirar um bloqueio são todos escritos no log de auditoria.

Uma câmera com gravações ainda sob um bloqueio não pode ser excluída. Colocar bloqueios exige a permissão **Bloquear gravações**, que as funções que podem exportar têm por padrão. As [notificações](/Help/alerts#notifications) podem avisar quando um bloqueio está para terminar ou foi removido.

## Capturas

**Capturas** lista toda detecção e tag de evento como uma miniatura, com filtros de câmera, grupo, visualização, tipo de objeto e hora. O movimento simples só aparece na linha do tempo. ▶ abre o instante na Reprodução.

## Atividade

**Atividade** mostra quando as coisas acontecem em uma câmera, ou em todas as câmeras juntas, como um mapa de calor por hora do dia. Escolha a câmera (ou **Todas as câmeras**) e um período: 24 horas, 7, 30 ou 90 dias, ou um ano.

- **Semana típica** (o padrão): as linhas são os dias da semana e as colunas são as horas, totalizados no período, então uma célula escura é um horário regularmente movimentado, como sábados das 18 às 19 h. Em mais de uma semana, passar o mouse sobre uma célula também dá a média por dia (por exemplo, "8,8 por sábado em média").
- **Por dia** (30 dias ou mais): cada data corre da esquerda para a direita com as horas de cima para baixo, e uma faixa de totais diários embaixo, para você identificar dias específicos.
- O dia mais movimentado do período e a hora mais movimentada são nomeados acima do gráfico.
- Para as últimas 24 horas, há uma linha para cada tipo de evento.
- **Tipos de evento** restringe ao que lhe interessa: um tipo inteiro (Animal) ou apenas alguns de seus rótulos (Animal → gato). O movimento simples vem desativado por padrão para não afogar os objetos. Sua escolha é lembrada e permanece quando você troca de câmera.
- Passe o mouse sobre uma célula para ver a contagem exata, ou ative **Mostrar como tabela** para ver os números.
- Os horários estão no fuso horário da câmera (definido na câmera), ou no do servidor se ela não tiver um. Todas as câmeras usa o fuso horário do servidor.
- O histórico de atividade é mantido por 400 dias (configuração `Activity.HistoryDays`), mesmo depois que as próprias gravações foram excluídas, então os períodos mais longos mostram o intervalo todo. O histórico se acumula desde que isto foi instalado: eventos já excluídos junto com suas gravações não podem ser recuperados.
- A visualização está no endereço da página, então você pode favoritá-la ou compartilhá-la. O widget **Atividade da câmera** do painel mostra os últimos 7 dias de uma câmera ou de todas.

### Cena

**Cena** mostra *onde* na imagem de uma câmera as coisas se movem: uma camada de calor sobre um quadro recente em tamanho real das gravações da câmera. Vermelho é onde mais acontece, depois laranja, amarelo, verde e azul para muito pouco. Os pontos sem nada permanecem limpos.

- **Objetos em movimento** conta quantos objetos passaram por cada ponto. Restrinja a **Pessoas**, **Veículos**, **Animais** ou **Outros**.
- **Movimento** mostra onde a detecção de movimento no servidor da câmera viu movimento, para câmeras que a usam no modo **Grade**.
- Escolha um período e, opcionalmente, uma **Hora do dia** (por exemplo, das 22h às 6h para as noites).
- O calor é somado uma vez por hora (o movimento a cada 10 minutos) e mantido pelo mesmo tempo que o resto do histórico de atividade. Os trajetos por trás dele são mantidos com seus eventos, então vão embora quando as gravações vão.
- Escolha uma câmera, ou uma **visualização** (listada em **Visualizações** na lista de câmeras) para ver lado a lado um mapa de calor de cada câmera dela. Cada câmera tem sua própria escala, então uma câmera tranquila ainda mostra onde está a sua atividade. Não pode ser mostrado para todas as câmeras de uma vez.
- **Plano de fundo**: se você pode configurar a câmera, pode substituir o quadro dela pela sua própria imagem (uma foto clara de dia, por exemplo) com **Usar minha própria imagem…**. Ela precisa ser da mesma câmera com o mesmo enquadramento, em largura total ou do mesmo formato, para que o calor se alinhe. **Usar a imagem da câmera** volta ao quadro automático.
- **Redefinir mapa de calor**: depois que uma câmera é movida ou reposicionada, o calor antigo dela não se alinha mais com a imagem. Se você pode configurar a câmera, **Redefinir mapa de calor** o limpa e conta apenas o que acontece daí em diante; o cartão mostra quando a contagem começou.

## Mapa

**Mapa** (em Ao vivo) mostra onde estão suas câmeras e para o que elas olham. Cada câmera é um ícone com um cone para o seu campo de visão: a direção para onde aponta, quão larga é a visão e a que distância. A cor mostra seu estado: verde é gravando, vermelho é offline, cinza é desativada. Clique em uma câmera para ver a imagem mais recente, com links para Ao vivo e Reprodução. Os status são atualizados a cada 30 segundos. O mapa reabre onde você o deixou, separadamente para cada pessoa e cada mapa.

Há dois tipos de mapa, e você pode ter quantos quiser de cada:

- **Mapa de ruas**: um mapa normal da região (OpenStreetMap por padrão). O alcance é em metros.
- **Planta**: uma imagem que você envia (PNG, JPEG, WebP ou GIF, até 10 MB), como uma planta de prédio ou um desenho do local. O alcance é em pixels dessa imagem.

Quem pode editar câmeras pode configurar mapas:

1. **Novo mapa → Mapa de ruas** ou **Planta**. Um novo mapa de ruas abre onde o atual está sendo exibido, ou no mundo todo; mova e aproxime até o seu local. Ao pressionar **Concluir edição**, o mapa é salvo para abrir onde está sendo exibido, para todos; **Gerenciar → Abrir este mapa aqui para todos** faz o mesmo sem editar. Isso vale também para plantas.
2. **Editar mapa**, depois arraste câmeras de **Câmeras que não estão neste mapa** para o mapa. Ou clique em uma para colocá-la no meio do mapa.
3. Clique em uma câmera para selecioná-la. Arraste o ícone para movê-la e arraste a alça branca na ponta do cone para apontá-la (direção e alcance). O painel lateral define o campo de visão, ou a direção e o alcance exatos, e **Tirar deste mapa** a remove. As alterações são salvas conforme você faz. Os botões de seta do painel a movem sem arrastar.

Uma câmera pode estar em vários mapas, como um mapa de ruas e a planta do prédio dela. As pessoas só veem as câmeras que têm permissão para ver.

As imagens do mapa de ruas são carregadas pelo seu navegador a partir do servidor de tiles, então um mapa de ruas exige acesso à internet pelo navegador. Para um local offline, ou outro estilo de mapa, defina seu próprio servidor de tiles https em **Gerenciar → Tiles do mapa de ruas**, e sua atribuição (os termos da maioria dos servidores de tiles exigem que ela seja mostrada no mapa). Alterar isso exige também a permissão de edição de Configurações, pois se aplica ao servidor todo.
