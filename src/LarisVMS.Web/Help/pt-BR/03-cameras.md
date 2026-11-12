# Câmeras

Adicionar câmeras, a lista de câmeras, streams, configurações do dispositivo, grupos, dispositivos com várias lentes e integrações de fabricantes.

## Adicionando uma câmera

Use **Câmeras → Descobrir** para varrer a LAN em busca de dispositivos ONVIF, ou **Câmeras → Adicionar** para informar a URL do serviço do dispositivo (por exemplo `http://192.168.1.50/onvif/device_service`) e as credenciais. O LarisVMS então **sonda** a câmera: lê seus recursos e streams ONVIF e escolhe os streams Principal e Secundário.

Atribua um **nó de gravação**, ou a câmera não gravará.

## A lista de câmeras

**Câmeras** lista todas as câmeras que você pode ver. A linha abaixo dos títulos das colunas filtra por qualquer coluna, como Marca, Modelo, Nó ou Grupo, e a caixa de busca acima da tabela filtra por texto em qualquer lugar de uma linha. Os filtros se combinam, e **Limpar filtros** os redefine. **Colunas** escolhe quais colunas são mostradas. Um filtro em uma coluna oculta é ignorado.

Seus filtros, colunas, ordem e tamanho de página são salvos na sua conta, então voltam quando você entra de novo, em qualquer dispositivo.

Marque câmeras para agir em várias de uma vez: **Reatribuir selecionadas ao nó**, ou **Configurações do dispositivo…** para câmeras da mesma marca e modelo (veja [Alterações em lote](#bulk-changes)). **Selecionar tudo** só seleciona as câmeras que correspondem aos filtros atuais.

## Sondar novamente

**Sondar novamente** na página de uma câmera lê seus recursos e streams outra vez. Faça isso depois de mudar a resolução, o codec ou o firmware da câmera na página web dela. As alterações feitas em [Configurações do dispositivo](#device-settings) sondam novamente de forma automática. Mudar a URL do dispositivo sonda novamente de forma automática ao salvar. **Configurações → Configurações das câmeras** pode sondar novamente todas as câmeras uma vez por dia. As câmeras são sondadas uma de cada vez, para que câmeras baratas não percam suas sessões de gravação. Os nomes de streams e os indicadores de ativação são mantidos numa nova sondagem.

## Streams

Cada câmera normalmente tem um stream **Principal** (resolução total, usado para gravação) e um stream **Secundário** (resolução menor, usado para blocos ao vivo pequenos e para a detecção de IA). Expanda **Dispositivo** na página da câmera para renomear ou desativar um stream. Passe o mouse sobre um stream para ver sua URL RTSP. O LarisVMS usa o endereço com que a câmera foi adicionada, mantendo a porta e o caminho que a câmera informa, de modo que uma câmera que informe um endereço interno (ou alguém se passando por ela) não consiga mandar o gravador para outro lugar com a senha dela.

## Configurações do dispositivo

**Configurações do dispositivo** na página de uma câmera altera as configurações da própria câmera por ONVIF, então você não precisa entrar na página web da câmera. As configurações são lidas ao vivo da câmera, e apenas as que ela suporta são mostradas.

| Seção | Configurações |
|---|---|
| **Streams de vídeo** | Para cada stream: codec (H.264, H.265 ou MJPEG), perfil de codificação, resolução, taxa de quadros, modo de taxa de bits (CBR ou VBR), taxa de bits, qualidade e intervalo de quadros-chave |
| **Imagem** | Brilho, contraste, saturação e nitidez; modo dia/noite; WDR; compensação de contraluz; modo de exposição; e balanço de branco |
| **Áudio** | Codec, taxa de bits e taxa de amostragem |

Cada seção tem seu próprio botão **Aplicar**, e apenas as configurações que você alterou são enviadas. Aplicar uma alteração de stream ou de áudio faz o stream se reconectar, o que deixa uma lacuna de alguns segundos na gravação; o LarisVMS então sonda a câmera novamente e o nó de gravação reconhece o novo formato de imediato. As alterações de imagem têm efeito sem reconectar.

Algumas câmeras compartilham um único conjunto de configurações de encoder entre streams. A página avisa, porque mudar um muda o outro.

H.265 e a escolha CBR/VBR exigem uma câmera com ONVIF Profile T (Media2). Em câmeras mais antigas essas opções não aparecem.

## Alterações em lote

Para alterar várias câmeras da mesma marca e modelo de uma vez:

1. Em **Câmeras**, filtre a coluna **Modelo** e depois marque as câmeras (ou **Selecionar tudo**).
2. Clique em **Configurações do dispositivo…**. Só está disponível quando todas as câmeras marcadas têm a mesma marca e modelo.
3. Altere o que precisar e clique em **Aplicar** naquela seção.

Você também pode partir da página **Configurações do dispositivo** de uma câmera e escolher outras câmeras do mesmo modelo em **Aplicar a**. Um selo *firmware diferente* marca câmeras com firmware distinto, que podem oferecer opções ligeiramente diferentes.

A página mostra as configurações atuais da primeira câmera. Apenas as configurações que você altera são enviadas a cada câmera, então o resto das configurações próprias de cada câmera permanece como está. Os streams são associados por função (Principal, Secundário ou Terceiro). Cada câmera confere a alteração com as próprias opções, e a lista de resultados mostra, para cada câmera, se foi aplicada, aplicada em parte, ignorada (por exemplo, uma câmera sem stream Terceiro) ou falhou (por exemplo, offline). **Tentar novamente as que falharam** envia a alteração de novo às câmeras que falharam.

## Grupos

Os grupos organizam câmeras em sites, prédios e andares. Um **grupo de nível superior é um site**, e os grupos abaixo dele são prédios ou andares. Uma câmera pode estar em qualquer número de grupos, desde que todos pertençam ao mesmo site. Os grupos servem para filtrar, para substituições de configurações e para o [acesso às câmeras](/Help/users#camera-access). O grupo integrado *Todas as câmeras* sempre contém todas as câmeras.

## Multicanal

Alguns dispositivos têm várias lentes em uma única carcaça. Quando uma sondagem encontra mais de uma fonte de vídeo, a página da câmera oferece **Dividir em N câmeras**. Cada lente então vira uma câmera própria, com gravação e retenção próprias. A câmera existente vira o canal 1 e mantém suas gravações.

## Integrações

Alguns recursos não estão disponíveis por ONVIF. As integrações (listadas em **Configurações → Plugins**) são associadas automaticamente pela marca e pelo modelo da câmera durante uma sondagem. Não há nada para ativar. Por exemplo, a integração **Eventos inteligentes Dahua / Amcrest** lê detecções de pessoas e veículos da análise da própria câmera. O painel **Dispositivo** da câmera mostra quando uma integração está ativa.
