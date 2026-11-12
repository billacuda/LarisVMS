# Armazenamento e retenção

Onde as gravações ficam, por quanto tempo são mantidas, o limite e o volume de arquivo morto.

## Caminhos de armazenamento

Cada nó grava no seu próprio **caminho de armazenamento**, definido na página de edição do nó ou na instalação. Não existe caminho global. Um nó sem caminho de armazenamento não grava nada, e a lista de nós o sinaliza.

## Retenção

A **retenção** é por quantos dias as gravações são mantidas. É definida globalmente em **Configurações → Armazenamento e retenção** e pode ser substituída por nó e por câmera; vale o valor mais específico. 0 mantém as gravações para sempre.

Uma câmera também pode ter uma **cota de armazenamento** em GB. Quando ela é ultrapassada, as gravações mais antigas da câmera são removidas primeiro.

## Limite

O **limite** é um teto rígido de quanto o disco de um nó pode encher (90% por padrão). Acima dele, o nó arquiva suas gravações mais antigas antes do prazo, ou as exclui se o arquivamento não estiver disponível, até o disco voltar para baixo do limite. A lista de nós mostra **armazenamento cheio** enquanto isso acontece.

## Arquivo morto

Um nó pode ter um segundo **caminho de arquivo morto** opcional, como um compartilhamento SMB ou uma unidade USB. Ele deve ser um local separado do caminho de armazenamento. Com o arquivamento ativado, as gravações que vencem pela retenção são **movidas** para o arquivo morto em vez de excluídas, e mantidas até a **retenção do arquivo morto** ser atingida. A retenção do arquivo morto é contada a partir da data original da gravação, e 0 mantém para sempre.

A reprodução, as miniaturas e as exportações funcionam do mesmo jeito a partir de qualquer volume. A lista de nós mostra o espaço livre e uma estimativa de dias restantes para os dois volumes. A estimativa se baseia nas últimas 24 horas de escrita.

## Gravações restantes

Quando uma câmera passa para outro nó ou é removida, suas gravações antigas ficam no nó anterior até vencerem. A contagem ⚠️ ao lado do status de um nó mostra quantas câmeras ainda têm gravações ali. Ela some sozinha.

## Criptografia

As gravações podem ser criptografadas nos discos dos gravadores, para que um disco roubado ou descartado não mostre nada. Vem desativada por padrão.

**Configure** em **Configurações → Criptografia e assinatura**:

1. **Criar chave mestra.** Ela fica neste servidor e destrava todas as gravações criptografadas.
2. **Baixe um backup** da chave mestra, protegido por uma frase secreta que você escolhe. A criptografia não pode ser ativada até você ter um. Guarde o arquivo e a frase secreta em um lugar diferente deste servidor: se o servidor for reconstruído, **Restaurar** com o backup é o único jeito de voltar a ler as gravações criptografadas. Perca os dois e as gravações se perdem para sempre.
3. **Escolha o que criptografar:** **Vídeo** (gravações e áudio de conversa bidirecional), ou **Vídeo, capturas e miniaturas**. Esse é o padrão de toda câmera; cada câmera pode substituí-lo na própria página (**Câmeras → Editar**, Armazenamento).

Cada segmento é criptografado no gravador logo depois de terminar de ser gravado, então apenas o segmento em gravação está sem criptografia no disco. As gravações feitas antes de a criptografia ser ativada ficam como estão; **criptografar gravações existentes** na página da câmera as converte em segundo plano. Desativar a criptografia depois deixa as gravações criptografadas legíveis.

A reprodução, as miniaturas, as capturas e as exportações funcionam como antes. Os arquivos exportados não são criptografados, já que se destinam a ser repassados. O gravador busca a chave diária de cada câmera neste servidor quando precisa e a mantém só na memória, então uma gravação criptografada não pode ser reproduzida enquanto o gravador não alcança o servidor.
