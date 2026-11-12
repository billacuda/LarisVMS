# Nós

Nós de gravação: status, caminhos de streaming, proxies de mídia, failover, manutenção e atualizações.

**Nós** na barra lateral lista todos os nós com seu status, endereço, versão, número de câmeras e uso do disco. Clique em um nó para editar suas configurações. O menu **⋯** tem estas ações:

- **Reiniciar serviço**: reinicia o serviço do Windows do nó, não a máquina. A gravação pausa por alguns segundos.
- **Redefinir autenticação**: limpa o token de check-in do nó, para um nó preso em falhas de autenticação após um heartbeat perdido. Não tem efeito em um nó saudável.
- **Manutenção**: move as câmeras do nó para o seu nó de reserva até você desativar a manutenção.
- **Excluir**: remove o nó. Suas câmeras ficam sem atribuição. As gravações não são excluídas.

Um nó está **online** se fez check-in nos últimos 2 minutos. Um aviso ⚠️ **relógio** significa que o relógio do nó está errado em mais de um minuto em relação ao do servidor, então verifique a sincronização de horário dele.

## Streaming direto

Por padrão, todo o vídeo ao vivo e de reprodução é retransmitido pelo servidor web (**Proxy**). Com **Direto**, os navegadores se conectam direto ao endpoint HTTPS de cada nó, o que alivia a carga do servidor. Defina o modo em **Configurações → Visualização ao vivo** e substitua-o por nó.

O modo Direto precisa de um **host do endpoint do cliente** que os navegadores consigam alcançar e de um certificado (um caminho `.pfx` na página do nó). **Permitir autoassinados** serve só para testes, porque os espectadores recebem um aviso do navegador. Uma câmera cujo nó não tem endpoint saudável volta automaticamente para o proxy.

## Conexões criptografadas do servidor

O servidor e os proxies de mídia alcançam as rotas de vídeo e de controle de cada nó por HTTP simples na sua porta ao vivo (8554), o que é aceitável em uma rede confiável. Para criptografar esse tráfego, ative **Criptografar conexões do servidor** na página do nó e depois reinicie o nó uma vez (**Reiniciar**). O nó inicia seu endpoint HTTPS (com o próprio certificado autoassinado se não tiver um `.pfx`) e informa o certificado, que o servidor então fixa: o servidor e os proxies aceitam do nó apenas esse certificado, e o token de mídia deixa de fazer parte do endereço. Depois de criptografado, a porta simples do nó responde apenas a `/health`.

A coluna **Criptografado** da página Nós mostra 🔒 para cada nó criptografado e ⏳ para um que foi ativado mas ainda precisa de uma reinicialização. O status abaixo da chave mostra o certificado em uso, ou que o servidor ainda o aguarda (até lá, o servidor continua usando HTTP simples, então nada deixa de funcionar). Vem desativado por padrão. Os certificados autoassinados usados para isso nunca são oferecidos aos navegadores; o streaming direto ainda precisa de um certificado de verdade.

## Proxies

Um **proxy de mídia** é um relay separado (`LarisVMS.Proxy`) entre navegadores e nós, para locais remotos ou links de baixa banda. Registre proxies em **Configurações → Proxies de mídia** e depois escolha um proxy principal e um de reserva na página de cada nó. Os navegadores usam o primeiro saudável.

## Failover

Dê a um nó um **nó de gravação de reserva**, e quando ele cair suas câmeras são gravadas pelo nó de reserva até ele voltar. "Fora do ar" é decidido por uma votação entre o parceiro do nó, o servidor e o proxy dele. A **Manutenção** move as câmeras de propósito e as mantém lá.

A página do nó avisa quando o nó de reserva tem hardware de IA diferente ou é somente de gravação. Nesses casos a detecção pode ficar mais lenta, ou pausar, durante um failover.

## Atualizações

Nós e proxies se atualizam sozinhos a partir de builds aprovadas (veja [Instalação](/Help/installation#updates)). **Nós → Atualizações e registro → Atualização automática** liga ou desliga isso para todos os nós.

A seção **Builds** da página Nós mostra a build para a qual cada tipo de nó caminha e qualquer build aguardando aprovação, com **Aprovar** e **Rejeitar**. **Histórico de builds** lista todas as builds, incluindo as aprovadas, as substituídas e as rejeitadas.

## Certificado do servidor

**Nós → Atualizações e registro → Verificação do certificado do servidor** decide como nós e proxies verificam o certificado HTTPS deste servidor:

- **Confiar no primeiro uso** (o padrão): um certificado de uma CA confiável sempre funciona, inclusive renovado ou substituído. Um certificado autoassinado é lembrado na primeira vez que um nó se conecta e continua funcionando enquanto permanecer o mesmo, até depois da data de expiração. Se mudar, nós e proxies o recusam até serem registrados novamente.
- **Somente certificado confiável**: apenas um certificado de uma CA confiável que corresponda ao nome do servidor. Instale um antes de escolher isto.
- **Não verificar**: qualquer certificado. Inseguro: qualquer pessoa no caminho da rede poderia se passar pelo servidor.

Com a confiança no primeiro uso, um nó confia em qualquer certificado que vê na primeira vez que se conecta. Para que ele verifique desde a primeiríssima conexão, passe ao instalador a impressão digital mostrada em **Verificação do certificado do servidor**: `-ServerCertThumbprint` para `install-node.ps1` e `install-proxy.ps1`, ou `SERVERCERTTHUMBPRINT=` para o MSI. Os comandos de instalação das páginas Configuração e Proxies de mídia já a incluem. Atrás de um proxy reverso, use a impressão digital do certificado que o proxy reverso apresenta.

O certificado autoassinado que o servidor cria para si dura 10 anos e não é substituído sozinho. **Regenerar certificado autoassinado** o substitui; todo nó e proxy precisa então ser registrado de novo, e a página Nós lista os que não fizeram check-in desde então. Para registrar um nó de novo, execute o instalador do nó outra vez, ou execute `LarisVMS.Node.exe --reset-server-pin` nele e reinicie o serviço (os proxies têm a mesma opção).

## Códigos de registro de uso único

Novos nós e proxies se registram com a **chave de registro** compartilhada ou com um código de uso único. Um código de uso único registra um único nó ou proxy e depois para de funcionar. Sem uso, expira após 1 hora, 24 horas ou 7 dias. Para criar um, vá em **Nós → Atualizações e registro**, escolha quanto tempo ele dura e selecione **Criar código**. O código é mostrado uma vez. Use-o onde o instalador pedir a chave de registro. **Revogar** cancela um código que ainda não foi usado.

A chave compartilhada funciona para toda instalação até ser alterada. Depois que você usar códigos de uso único, desmarque **Aceitar a chave de registro compartilhada** para que uma cópia da chave em um script ou e-mail antigo não registre nada. Nós e proxies já registrados não são afetados. De qualquer forma, um novo nó ainda aguarda aprovação antes de receber câmeras.
