# Detecção de IA

Detecção de objetos no nó: modelos, hardware, limites, capturas e o backend externo.

Cada nó pode executar detecção de objetos em tempo real nas suas câmeras. Os objetos detectados recebem caixas delimitadoras ao vivo, uma marca colorida na linha do tempo, um selo no bloco ao vivo e uma captura recortada. Uma detecção também conta como movimento para a gravação no modo Movimento. A detecção roda em um processo separado, então um problema de GPU ou de driver nunca interrompe a gravação.

Ative por câmera com **Observar objetos nesta câmera**. Bastam **5–7 fps por câmera** para uma detecção precisa, então limite a [taxa de quadros](#thresholds) nesse valor para manter baixa a carga da GPU. Os padrões globais ficam em **Configurações → Detecção de IA**. Nós e câmeras podem substituir a maioria deles.

## Hardware

Um único pacote de nó atende todas as máquinas. Na inicialização, o serviço de visão escolhe um backend para o hardware que encontra:

| Hardware | Backend | Necessário no nó |
|---|---|---|
| GPU NVIDIA | CUDA (recua para DirectML) | CUDA Toolkit 12.x e cuDNN 9.x |
| GPU AMD / Intel | DirectML | Um driver de GPU atual |
| Sem GPU | CPU | Nada |

A página de edição do nó mostra os aceleradores detectados, e sua configuração **Acelerador** pode forçar um. Os modelos nunca vêm incluídos. Um nó busca os modelos YOLOX integrados do servidor, e outros modelos vão em `C:\ProgramData\LarisVMS\models` no nó.

## Backend lento

Se um nó NVIDIA não pode usar CUDA, a detecção de IA recua para o DirectML, que é várias vezes mais lento. Menos quadros são analisados, a GPU esquenta e objetos rápidos, como carros passando, podem não ser detectados. A página **Nós** então mostra **⚠️ IA no DirectML** ao lado do nó, com o motivo, e os Super Admins recebem uma notificação.

A causa usual é a falta do cuDNN. Instale o CUDA Toolkit 12.x e o cuDNN 9.x para CUDA 12 no nó, pelo download do cuDNN da NVIDIA ou com `pip install nvidia-cudnn-cu12`. O nó os encontra em até 10 minutos, copia-os para junto do seu backend de IA e reinicia a detecção no CUDA.

## Famílias de modelos

O modelo roda uma vez por nó e atende todas as câmeras desse nó, então as configurações do modelo são por nó, não por câmera.

- **Auto / YOLOX**: o padrão. Rápido, não precisa de letterbox e funciona bem em GPUs de baixo consumo e que não sejam NVIDIA. **Tamanho do YOLOX** vai de Nano (o mais rápido) a X (o mais preciso). Use Nano ou Tiny em máquinas de baixo consumo e L ou X em GPUs potentes.
- **D-FINE**: experimental. Os resultados podem ser imprevisíveis, e objetos que ficam entre dois tiles no modo Slice podem ser perdidos ou duplicados. **Pesos**: o Obj2Coco conhece 80 tipos de objeto (pequeno ou médio), e o Obj365 conhece 365.
- **Personalizado**: qualquer modelo `.onnx` colocado na pasta de modelos do nó. Veja [Modelos personalizados](#custom-models).

Mudar o modelo reinicia a detecção de cada câmera.

## TensorRT

**D-FINE TensorRT** só faz algo quando o nó tem o TensorRT ativado (`Vision:EnableTensorRt`) e o SDK do TensorRT 10.x instalado. **FP32** dá um ganho seguro. **FP16** é o mais rápido; se o motor de uma câmera estourar em FP16, ele é reconstruído em FP32 automaticamente. A primeira inicialização em cada resolução de câmera leva vários minutos para construir o motor.

## Modelos personalizados

Coloque um arquivo `.onnx` em `C:\ProgramData\LarisVMS\models` no nó. Se os metadados do próprio modelo descrevem seu decodificador, tamanho de entrada e rótulos, ele simplesmente funciona. Caso contrário, adicione um arquivo `.json` com o mesmo nome que forneça o que falta. **Atualizar** lista todos os modelos que os nós encontraram e sinaliza aqueles cujos metadados estão incompletos. Um modelo com aviso pode ser selecionado, mas não será executado até o aviso ser resolvido.

## Ajuste de proporção

Como o quadro de uma câmera se encaixa na entrada quadrada do modelo:

- **Letterbox** (padrão): mantém as proporções e preenche com barras pretas.
- **Esticar**: amassa o quadro para preencher o quadrado, o que distorce as formas.
- **Slice**: corta o quadro em quadrados sobrepostos de resolução total. É o melhor para objetos pequenos ou distantes em câmeras grande-angulares, mas custa cerca de 2× o trabalho numa câmera 16:9. Exige pré-processamento por GPU, e o ativa automaticamente.

O **pré-processamento de quadros por GPU** tira a conversão de cores da CPU e a coloca na GPU, o que ajuda em nós com muitas câmeras.

## Limites

- **Confiança**: a pontuação mínima para uma detecção contar. Menor detecta mais objetos, mas adiciona falsos positivos. Aumente numa câmera que insiste em detectar algo errado.
- **IoU**: quanto duas caixas precisam se sobrepor para serem mescladas como um só objeto.
- **Stream de detecção**: Secundário é mais leve; Principal enxerga mais detalhes.
- **Orientação**: algumas câmeras montadas em corredores informam um stream em paisagem mas enviam vídeo em retrato. Defina **Retrato** nessas câmeras para que a detecção e as capturas não fiquem achatadas.
- **Taxa de quadros máxima de detecção**: quadros por segundo por câmera que chegam ao modelo. **5–7 fps bastam para detecção e rastreamento precisos.** Taxas maiores aumentam a carga da GPU sem melhorar os resultados. 0 é sem limite.

## Capturas

Cada objeto rastreado recebe uma captura, recortada do seu melhor quadro.

- **Tempo limite de inatividade**: por quanto tempo um objeto pode ficar sem ser visto antes de sua captura se fechar.
- **Informar objetos parados**: informa também objetos que estão presentes mas não se movem, como um carro estacionado.
- **Precisão de movimento da captura**: fecha uma captura logo depois que seu objeto sai, para que um objeto posterior do mesmo tipo não seja mesclado a ela. **Tolerância de saída** é quanto esperar. Se for curta demais, um objeto que faz uma pausa é dividido em duas capturas.
- **Margem de corte da captura**: espaço extra ao redor do objeto, como porcentagem da sua caixa.

Vários objetos do mesmo tipo em uma captura aparecem como uma contagem (×2). Cada um também é mantido separadamente, com suas próprias cores e velocidade.

## Cores dos objetos

A cor de cada objeto rastreado é lida do seu quadro mais nítido: uma cor para veículos, animais e outros objetos, e cores de camisa e calça para pessoas. As capturas mostram um selo para cada uma, pintado nessa cor, e as seções **Cor**, **Camisa** e **Calça** do filtro de Capturas restringem os resultados às cores que você marca. Os alertas de detecção também podem filtrar por cor.

As cores vêm da imagem, não do modelo, então funcionam com qualquer detector. As cores logo fora da caixa também são amostradas e deixadas de lado, para que o gramado ao redor de um cachorro ou a rua ao redor de um carro não vençam. Ainda assim são palpites no limite do possível: cenas movimentadas, objetos sobrepostos e iluminação podem dar a cor errada. A iluminação é o que mais conta: sombra, faróis e balanço de branco alteram as cores, e um carro cinza-claro pode parecer prata ou branco. Enquanto uma câmera está no modo noturno preto e branco (IV), nenhuma cor é registrada. Desative com **Cores dos objetos** em **Configurações → Detecção de IA**.

## Controles de velocidade

Um controle de velocidade cronometra objetos entre duas linhas atravessando uma via. Na página **Controles de velocidade** da câmera (em **Câmeras → Editar**), clique em **Novo controle de velocidade**, clique nas duas pontas da linha **A**, depois nas duas pontas da linha **B**, e informe a distância real entre as linhas, medida ao longo da via. Todo objeto rastreado que cruza as duas linhas recebe uma velocidade, mostrada na sua caixa ao vivo e na sua captura. **Sentido** cronometra nos dois sentidos por padrão; escolha **Somente de A para B** ou **Somente de B para A** para cronometrar uma faixa de uma via de mão dupla. As **Unidades de velocidade** (km/h ou mph) ficam em **Configurações → Detecção de IA**.

Um objeto que um controle cronometra é registrado como em movimento mesmo que de outro modo pareça parado, como um veículo vindo direto na direção da câmera. A cronometragem usa o meio da parte inferior de cada caixa, onde o objeto toca o chão, então desenhe as linhas na superfície da via. Para uma boa leitura:

- Desenhe as linhas atravessando o sentido do trânsito, em solo plano, e o mais afastadas que a imagem permitir, desde que os objetos continuem bem à vista.
- Meça a distância com cuidado: conte os passos ou use a ferramenta de medição de um mapa.
- Confira uma vez com um veículo passando a uma velocidade conhecida e ajuste a distância se a leitura estiver errada.

As leituras são estimativas. A taxa de quadros de detecção da câmera limita a precisão com que uma travessia é cronometrada, então controles curtos e veículos rápidos são os menos precisos. Salvar um controle reinicia a detecção de IA da câmera.

Um controle pode ter um **limite de velocidade** (opcional). As velocidades acima dele são mostradas em vermelho e contadas separadamente no mapa de calor de velocidades.

As velocidades medidas aparecem em vários lugares:

- **Visualização ao vivo**: na caixa de cada objeto enquanto ele está à vista.
- **Capturas**: um selo ⏱ no cartão e um filtro de **Velocidade** ("igual ou acima de") na lista de filtros.
- **Reprodução**: uma marca na linha do tempo a cada medição (vermelha quando acima do limite; passe o mouse para ver detalhes) e um selo sobre o vídeo enquanto o objeto está na tela.
- **Painel**: o widget **Registro de velocidades** mostra as medições mais recentes. Clique no título para o registro completo, com ordenação, filtro e download em CSV.
- **Atividade**: mude para **Velocidades** para um mapa de calor de velocidade média, velocidade máxima, quantidade acima do limite ou quantidade medida, por dia e hora. O histórico é mantido pelo mesmo tempo que o histórico de atividade, muito depois de as gravações terem sumido.
- **Alertas**: uma regra "Velocidade acima de um limite".

## Movimento

As capturas, os alertas e as caixas **Em movimento** ao vivo só contam objetos que estão indo a algum lugar. A caixa de um detector treme um pouco de quadro a quadro mesmo ao redor de um carro estacionado, então alguns pixels de mudança não bastam. **Detecção de movimento** em **Configurações → Detecção de IA**, ou por câmera em **Câmeras → Editar**, decide como isso é julgado.

**Padrão** (o padrão) examina o formato do trajeto recente de cada objeto:

- Qualquer coisa que se mova de forma constante em uma direção conta em poucos quadros (cerca de meio segundo a 7 fps), seja um carro cruzando a imagem em menos de um segundo ou um caminhão saindo de uma garagem. Os passos de cada objeto são comparados com o quanto a própria caixa dele treme, então um objeto grande não precisa percorrer mais que um pequeno.
- Algo que avança mais devagar do que sua caixa treme, uma fração do passo de uma caminhada, conta depois que seu trajeto seguiu uma linha constante por alguns segundos. O tremor vai e volta, então nunca forma uma linha.
- A distância que um objeto precisa percorrer cresce com seu tamanho, mas mais devagar que o próprio tamanho. Um caminhão grande não precisa antes cruzar metade do próprio comprimento.
- Um objeto entrando pela borda da imagem, ou saindo de trás de um poste ou árvore, é julgado pelo lado da sua caixa que está à vista.
- Um objeto vindo direto para a câmera ou se afastando conta à medida que cresce ou diminui.
- A caixa de um carro estacionado mudando quando alguém passa na frente não conta.

A **Sensibilidade ao movimento** (apenas Padrão) é **Normal** por padrão. **Alta** detecta movimento mais cedo e **Baixa** ignora mais tremor. Reduza numa câmera em que um carro estacionado ou um arbusto balançando seja informado como em movimento, e aumente onde objetos lentos são perdidos.

Uma caixa que apenas muda de formato, como a de uma planta em vaso quando o detector inclui as folhas num momento e não no seguinte, ou a de um carro estacionado quando algo passa na frente, não conta. O movimento precisa se acumular ao longo dos quadros, não acontecer num único salto.

**Legado** é a regra anterior: um objeto conta quando seu centro se moveu metade do tamanho da sua caixa em 2 segundos. Use-o se o Padrão se comportar mal numa câmera.

Um objeto que estava em movimento e some atrás de uma árvore, de um poste ou de um carro estacionado continua sendo o mesmo objeto quando reaparece mais adiante na mesma linha em cerca de 30 quadros analisados (cerca de 5 segundos a 6 fps), então é contado uma vez e mantém um só trajeto. Ele também precisa ser do mesmo tipo de objeto e de tamanho parecido, e sua cor precisa coincidir quando as duas puderem ser determinadas. Algo que reaparece indo em outra direção, ou depois de mais tempo, é um objeto novo.

## Objetos em movimento

O trajeto de cada objeto em movimento pela imagem é mantido com seu evento, para o mapa de calor **Cena** da página Atividade.

**Objetos em movimento** em **Configurações → Detecção de IA** escolhe que tipos de objeto podem contar como em movimento. Por padrão são **Pessoas**, **Veículos** e **Animais**. Todo o resto ainda é detectado e aparece como uma caixa parada com **Parado** ativado, mas nunca conta como em movimento, então não pode iniciar uma captura nem um alerta. Escolha outros objetos um a um na lista **Outros objetos**: um pacote entregue costuma aparecer como mala, mochila ou bolsa. Para um modelo personalizado, digite os próprios nomes de objetos, separados por vírgula.

## Objetos ignorados

**Objetos ignorados** em **Configurações → Detecção de IA** desliga completamente certos tipos de objeto. Eles são descartados assim que o modelo os informa, então nunca recebem caixa, captura, evento nem alerta. Use para coisas que o modelo insiste em errar nas suas câmeras, como um deck que ele chama de avião. Marque categorias inteiras (**Pessoas**, **Veículos**, **Animais** e **Outros objetos**) ou objetos individuais da lista. A grafia de qualquer um dos modelos corresponde, então ignorar **airplane** também ignora **aeroplane**. Para um modelo personalizado, digite os próprios nomes de objetos, separados por vírgula.

A página **Editar** de uma câmera pode escolher a própria lista em vez da global: escolha **Escolher para esta câmera** em **Objetos ignorados**. Não escolher nada ali detecta tudo naquela câmera, até o que a lista global ignora. Salvar reinicia a detecção daquela câmera, o que apenas redefine o rastreamento.

## Jitter

O **limite de jitter** é o menor movimento em pixels que chega a contar, seja qual for o tamanho do objeto. No modo Legado, a **rejeição de jitter de objetos parados** suaviza o tremor de um veículo estacionado distante; ela também pode manter como parado um objeto realmente lento, por isso fica desativada por padrão. Não se aplica ao Padrão, que trata o tremor por conta própria.

## Backend externo

**Serviço HTTP externo** não executa nenhum modelo no nó. Cada quadro é enviado a um servidor de inferência que você mantém, que devolve as caixas em JSON. Use para modelos que o LarisVMS não inclui, ou para nós sem GPU. O rastreamento, as capturas, a linha do tempo e a sobreposição ao vivo funcionam do mesmo jeito.

O serviço precisa expor `POST /v1/detect`, `GET /v1/models` e `GET /healthz`. **Testar** verifica a conexão e carrega a lista de modelos. A **chave de API** é enviada como token Bearer. **Transporte**: o JPEG funciona com qualquer serviço. Os modos brutos YUV e BGRA dispensam a codificação JPEG mas usam 4–10× a banda, e só funcionam se o modelo os anunciar.
