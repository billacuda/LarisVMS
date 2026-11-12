# Gravação

Modos de gravação, agendamentos, tags de evento, pré/pós-gravação e duração dos segmentos.

## Modos

Toda câmera grava continuamente para o seu nó. O **modo de gravação** decide quais gravações são *mantidas*:

| Modo | Mantém |
|---|---|
| **Contínua** | Tudo. |
| **Movimento** | As gravações em torno de movimento: de uma zona de movimento, dos eventos de movimento da própria câmera, de uma integração ou da detecção de IA (veja [Movimento](/Help/motion#source)). |
| **Agendamento** | As gravações que começam dentro de uma janela de agendamento ativada. |
| **Evento** | As gravações em torno de uma regra de tag de evento com *Conduz a gravação* ativado. |
| **Somente manual** | Nada, a menos que uma [gravação manual](#manual-recording) esteja ativa. A câmera fica apenas em visualização ao vivo no resto do tempo. |

As gravações que não são mantidas são excluídas logo depois de escritas, em vez de esperar pela retenção.

Os modos Movimento, Agendamento e Evento **falham aberto (fail open)**. Até uma câmera ter uma zona de movimento, uma janela de agendamento ou uma regra que conduza a gravação, ela mantém tudo em vez de descartar. A página da câmera avisa quando isso acontece. Somente manual não falha aberto, porque não manter nada é a sua finalidade.

Defina o modo global em **Configurações → Padrões de gravação** e substitua-o na página de uma câmera.

## Gravação manual

Uma gravação manual mantém as gravações de uma câmera até alguém pará-la, seja qual for o modo de gravação da câmera. Inicie uma com o botão de gravação em um [bloco ao vivo](/Help/live-and-playback#live-tiles) ou pela [API](/Help/api#recording). Ela continua ativa após reinicializações e atualizações.

Funciona junto com o modo da câmera. Uma câmera em modo Movimento mantém as gravações quando vê movimento *ou* quando uma gravação manual está ativa, e os eventos continuam etiquetando a linha do tempo como de costume. Em uma câmera Contínua, ela apenas marca a linha do tempo.

- As gravações são mantidas em segmentos inteiros, então até um segmento antes e depois da gravação também é salvo. Pré-gravação e pós-gravação não se aplicam.
- As gravações salvas seguem a retenção normal da câmera. Adicione um [bloqueio de provas](/Help/live-and-playback#evidence-locks) para mantê-las por mais tempo.
- Aparece na linha do tempo como uma faixa vermelha. Mude a cor em **Configurações → Eventos**.
- Uma câmera offline não salva nada até voltar. A gravação continua ativa.
- Atualize os nós de gravação junto com o servidor. Um nó mais antigo não conhece gravações manuais.

## Pré-gravação e pós-gravação

Nos modos Movimento e Evento, a **pré-gravação** é quanta gravação antes da atividade é mantida (para ver alguém entrando), e a **pós-gravação** é quanta depois do seu fim.

## Duração dos segmentos

As gravações são escritas como arquivos de duração fixa (60 segundos por padrão). A reprodução precisa baixar parte de um segmento antes de poder começar no instante exato que você escolhe. Segmentos mais curtos tornam a busca mais rápida em câmeras de alta resolução, mas criam mais arquivos. Mudar a duração reinicia brevemente a gravação da câmera.

## Agendamento

A página **Agendamento** de uma câmera guarda suas janelas: dias, uma hora de início e uma hora de término. Uma hora de término anterior à de início cruza a meia-noite. Os horários usam o **relógio local do nó de gravação**. As janelas só importam no modo Agendamento.

## Tags de evento

A página **Tags de evento** de uma câmera transforma eventos ONVIF da câmera (movimento, alarmes, análise, violação) em marcas coloridas na linha do tempo.

- **Tópico de início**: o evento que inicia a tag. Escolha um em *Tópicos observados*, que lista tudo que a câmera realmente enviou.
- **Tópico de parada**: opcional. Deixe em branco quando o tópico de início carrega o próprio estado verdadeiro/falso (a maioria dos tópicos de movimento e alarme carrega). Defina quando a câmera enviar eventos de início e de parada separados.
- **Conduz a gravação**: nos modos Evento ou Movimento, as gravações em torno desta tag são mantidas.

Uma tag termina apenas quando seu evento de parada chega, nunca por tempo limite.
