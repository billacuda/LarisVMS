# Detecção de movimento

Fontes de movimento, movimento no servidor, zonas e a grade de movimento.

## Fonte

Uma câmera pode informar movimento de várias formas:

- **Movimento no servidor**: o nó compara quadros de vídeo dentro de zonas de movimento ou da grade de movimento.
- **Eventos da câmera**: os próprios eventos de movimento ONVIF da câmera.
- **Integração do fabricante**: por exemplo, eventos inteligentes Dahua / Amcrest.
- **Detecção de IA**: um objeto em movimento detectado.

A **Fonte de movimento** da câmera escolhe qual *uma* delas decide o que o modo Movimento mantém, para que a linha do tempo não tenha entradas duplicadas do mesmo evento. **Auto** escolhe a mais rica que estiver configurada. As regras de tags de evento e os rótulos de objetos da IA sempre etiquetam a linha do tempo, independentemente dessa escolha.

## Movimento no servidor

**Movimento no servidor** na página da câmera liga ou desliga a detecção de movimento por pixels do próprio nó. Mantenha ligado para etiquetar movimento simples na linha do tempo como alternativa, mesmo quando outra fonte controla a gravação. Desligue para economizar a CPU que ele usa.

## Zonas

A página **Zonas** de uma câmera tem dois métodos. Só um fica ativo por vez, e alternar mantém as configurações do outro.

**Grade**: clique em células para mascará-las (vermelho). As células ficam âmbar enquanto há movimento nelas, o que ajuda a achar um galho balançando ou uma luz piscando para mascarar. Mudar o tamanho da grade limpa a máscara.

**Zonas poligonais**:

- **Movimento**: uma área para observar.
- **Ignorar**: uma área para excluir (uma árvore, uma calçada pública). Os pixels em uma zona Ignorar nunca contam, mesmo onde ela se sobrepõe a uma zona de Movimento.
- **Privacidade**: uma área para ocultar. Desenhe-as em **🔒 Máscaras de privacidade**, que funciona seja qual for o método de movimento (Grade ou Zonas poligonais) que a câmera use. Fica oculta nas gravações, na visualização ao vivo (os dois streams), nas capturas e nas exportações. Os eventos continuam acontecendo atrás dela como de costume (as capturas deles são mascaradas como o vídeo), a menos que você ative **Ignorar também detecções dentro de máscaras de privacidade** para aquela câmera, na visão Máscaras de privacidade. A máscara segue o formato que você desenha, com uma pequena margem para que nenhuma borda apareça. Uma câmera com uma zona de privacidade tem o vídeo recodificado durante a gravação, o que usa um pouco de CPU (ou a GPU, quando o nó tem uma compatível).
- As zonas de movimento **do lado da câmera** podem ser salvas, mas ainda não estão ativas.

Para remodelar uma zona, selecione-a na lista e arraste seus cantos, depois **Salve**. Enquanto desenha, o clique direito remove o último ponto. Passar o mouse sobre uma zona na lista a destaca na imagem. Você também pode clicar em um canto e depois clicar onde ele deve ir.

A **Sensibilidade** é a porcentagem dos pixels da zona que precisam mudar. Menor é mais sensível. Zonas largas precisam de valores menores, porque o movimento real cobre só uma pequena parte delas. Comece baixo (cerca de 3%) e aumente só se vento ou mudanças de iluminação a dispararem.
