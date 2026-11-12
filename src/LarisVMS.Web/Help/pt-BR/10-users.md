# Usuários e acesso

Contas, funções, permissões, acesso às câmeras, Active Directory e login com a Microsoft.

## Usuários

O autocadastro está desativado. As contas são criadas em **Configurações → Usuários**, onde você também pode atribuir funções, desativar uma conta e redefinir uma senha. As contas locais entram com o endereço de e-mail. Desativar uma conta a desconecta de todos os lugares na hora e encerra seu vídeo ao vivo.

## Senhas

As senhas têm pelo menos 12 caracteres. As comuns, as como `123456789012` e as que contêm o nome ou o e-mail da pessoa são recusadas. Para recusar mais, coloque-as em `%ProgramData%\LarisVMS\password-blocklist.txt` no servidor web, uma por linha (uma lista publicada de senhas vazadas serve). É reconhecido sem reiniciar.

Quem tem uma conta local altera a própria senha selecionando o próprio nome no alto da página. A senha atual é exigida, e todas as outras sessões da pessoa são encerradas. Os administradores alteram a própria senha do mesmo jeito, não em **Configurações → Usuários**. As contas do Active Directory alteram a sua no Windows.

Ao criar uma conta ou redefinir uma senha em **Configurações → Usuários**, você pode marcar **Precisa alterá-la no próximo login**. A pessoa então tem que escolher a própria senha antes de poder fazer qualquer outra coisa. Com **Configurações → Segurança → Senhas temporárias definidas pelo administrador** ativado, ela já vem marcada.

Após 5 senhas erradas para uma conta vindas de um endereço em 15 minutos, esse endereço precisa esperar os 15 minutos passarem; a pessoa ainda pode entrar de qualquer outro lugar. Uma conta só é bloqueada por 15 minutos depois de 20 tentativas erradas vindas de qualquer lugar, e os Super Admins nunca são bloqueados por senhas erradas no login. Os Super Admins são avisados de falhas repetidas e bloqueios, e de tentativas espalhadas por muitas redes: uma conta falhando 10 vezes a partir de 3 ou mais redes, ou 50 falhas a partir de 10 ou mais redes no servidor todo, em 15 minutos.

## Login em duas etapas

Quem entra com senha, local ou do Active Directory, também pode exigir um código de seis dígitos de um app autenticador no celular (Microsoft Authenticator, Google Authenticator, 1Password e outros). Cada código funciona uma vez, então um que alguém veja sendo digitado não pode ser reutilizado. Para configurar, selecione seu nome no alto da página, depois **Login em duas etapas**, escaneie o QR code e digite o código que o app mostra. Você recebe dez códigos de recuperação; guarde-os em um lugar seguro, pois cada um permite um login se você perder o celular. Alterar qualquer coisa depois, como novos códigos de recuperação ou desativar, exige um código atual.

No login, **Não perguntar novamente neste navegador por 14 dias** pula o código naquele navegador até a senha ou a configuração de duas etapas mudar.

Se alguém perder o celular e os códigos de recuperação, **Desativar 2FA** ao lado do nome da pessoa em **Configurações → Usuários** permite que ela entre só com a senha e configure de novo.

**Configurações → Segurança → Login em duas etapas** obriga todos que entram com senha a configurá-lo antes de poderem fazer qualquer outra coisa, e impede que o desativem. Os logins da Microsoft ficam a cargo das verificações do próprio Entra.

## Sessões

Selecione seu nome no alto da página, depois **Sessões**, para ver todos os navegadores em que você está conectado, com o endereço e o último uso. Encerre os que você não reconhecer, ou todos menos o que está usando, e depois altere sua senha. Alterar a senha ou ativar o login em duas etapas encerra suas outras sessões na hora.

Com **Configurações → Segurança → Confirmar a senha para páginas de administração** ativado, as páginas de administração pedem sua senha de novo (e um código, com o login em duas etapas ativado) a menos que você tenha entrado ou confirmado nos últimos 15 minutos; usá-las mantém isso valendo. Quem encontra seu navegador conectado pode então usar o sistema, mas não alterar suas configurações, usuários nem chaves. Os logins da Microsoft entram de novo. Vem desativado por padrão.

Em **Configurações → Usuários**, **Encerrar sessões** ao lado de uma pessoa a desconecta em todos os navegadores. Ela pode entrar de novo; desative a conta para mantê-la fora.

## Funções

Uma função é um conjunto nomeado de permissões, gerenciado em **Configurações → Funções**. Cada função também tem:

- um **nível de escopo**;
- uma **expiração** para acesso por tempo limitado (por exemplo, 480 minutos para um turno de 8 horas);
- uma **duração de sessão**: horas depois do login antes de o usuário ter que entrar de novo, por mais ativo que esteja (no máximo 30 dias, ou 0 para nunca expirar);
- um **tempo limite de inatividade**: minutos sem abrir uma página antes de o usuário ser desconectado (0 desativa). Uma tela mostrando vídeo ao vivo conta como inativa, então deixe desativado para funções que assistem a um painel de vídeo;
- uma **prioridade PTZ**, que decide quem vence quando dois usuários movem a mesma câmera.

## Permissões

**Configurações → Matriz de permissões** define quais ações cada função pode realizar (ver câmeras, reprodução, exportação, editar configurações e assim por diante). A função integrada **Super Admin** sempre tem todas as permissões.

**Iniciar e parar a gravação manual** controla o botão de gravação nos blocos ao vivo. Ela também exige o acesso **Gravação** às câmeras abaixo.

## Acesso às câmeras

**Configurações → Acesso às câmeras** decide quais câmeras cada função pode usar: todas as câmeras, um grupo ou câmeras individuais, separadamente para Ver, Reprodução, Exportação, PTZ, Falar (áudio bidirecional), Configurar e Gravar (gravação manual). Uma função **sem concessões não tem câmeras**; o Super Admin sempre tem todas as câmeras. A exportação nunca vai além da reprodução. Atualizar de uma versão em que funções sem concessões podiam usar todas as câmeras adiciona uma concessão "todas as câmeras" para manter o acesso de cada função e usuário igual.

## Chaves de API

**Admin → Chaves de API** emite chaves para a [API](/Help/api). Cada chave age com a função à qual está vinculada e pode ser limitada a algumas das permissões dessa função, então várias chaves podem fazer coisas diferentes. O acesso às câmeras da função decide quais câmeras uma chave pode usar.

## Entra

**Configurações → Segurança → Login do Entra ID** adiciona um botão "Entrar com a Microsoft". Ele nunca cria contas e não associa contas por e-mail: em **Configurações → Usuários**, use **Login com a Microsoft** na linha de uma pessoa para informar o **ID de objeto** do Entra dela (da página do usuário no centro de administração do Microsoft Entra). O primeiro login dela com a Microsoft conclui o vínculo; em branco o remove. Registre um app no Entra com o URI de redirecionamento mostrado na página Segurança e depois informe o ID de locatário dele (o GUID ou domínio do seu diretório, não `common`), o ID do cliente e o segredo. As alterações têm efeito imediato. As contas já vinculadas continuam funcionando.

## Active Directory

**Configurações → Active Directory** permite que usuários do domínio entrem com o nome de usuário e a senha do AD e lhes dá funções a partir dos seus grupos do AD. Somente um Super Admin pode alterar o domínio, como o LarisVMS se conecta a ele ou o login local, pois eles decidem quem pode entrar como qualquer pessoa; outros administradores podem alterar o intervalo de sincronização e os vínculos de grupos.

- **Domínio:** apenas o nome do domínio, como `corp.example.com`.
- **Criptografia:** LDAPS na porta 636 é o padrão e exige nos seus controladores de domínio um certificado em que este servidor confie. O LDAP simples na porta 389 funciona sem ele, mas é inseguro e não é recomendado.
- **Conta de busca:** a segurança integrada (o padrão) usa a própria conta do serviço LarisVMS Web. Quando o serviço roda como LocalSystem em um servidor ingressado no domínio, essa é a conta do computador. Ou informe uma conta de serviço como `DOMINIO\usuario` com a senha dela.
- **Entrando:** um nome com `@` é uma conta local. Qualquer outra coisa é um nome de usuário do AD, digitado sem o domínio.

**Vínculos grupo → função:** comece a digitar o nome de um grupo e escolha na lista (Tab pega a primeira correspondência). Os membros do grupo recebem a função, inclusive os membros de grupos aninhados. As contas são criadas na primeira vez que são necessárias, assim que um grupo é vinculado ou quando o usuário entra pela primeira vez. Grupos e usuários são rastreados por SID, então renomear qualquer um deles no AD é reconhecido na próxima sincronização. As funções das contas do AD vêm só dos vínculos de grupos e não podem ser editadas na página Usuários.

**A sincronização** roda a cada 30 minutos por padrão (de 5 minutos a 24 horas), ou na hora com **Sincronizar agora**. Uma conta do AD desativada ou expirada no AD, ou que não está mais em nenhum grupo vinculado, é desativada aqui e desconectada. Se o AD não puder ser alcançado, a sincronização não altera nada e tenta de novo em 5 minutos. Uma conta desativada pela sincronização é reativada quando volta a se qualificar. Uma desativada à mão na página Usuários permanece desativada.

**O login local** pode ser desativado enquanto o login do AD estiver ativado, depois que pelo menos uma conta do AD tiver Super Admin. Guarde em um lugar seguro a conta de Super Admin criada durante a configuração e sua senha. Se o AD ficar indisponível, execute isto no banco de dados do LarisVMS e entre com essa conta. No SQL Server:

```sql
UPDATE ActiveDirectorySettings SET LocalLoginsEnabled = 1
```

No MariaDB:

```sql
UPDATE `ActiveDirectorySettings` SET `LocalLoginsEnabled` = 1;
```

## Log de auditoria

Tudo o que os usuários fazem é registrado em **Logs → Logs de auditoria**: alterações (com valores antigos e novos; segredos são registrados só como "alterado"), visualização de câmeras, início de reprodução e exportações.

**À prova de adulteração.** Cada entrada é selada junto com a anterior, usando uma chave que o banco de dados sozinho não possui. A cada 6 horas (e quando você seleciona **Verificar agora** na página do log de auditoria) o log inteiro é verificado. Se uma entrada foi alterada, removida ou adicionada diretamente no banco de dados, a página diz qual e os Super Admins são avisados. As entradas removidas pela retenção são esperadas e não contam, e cada remoção é, ela mesma, registrada (**AuditLog.Pruned**). As entradas anteriores a esta atualização não são cobertas.

O servidor também guarda um pequeno registro fora do banco de dados, em `%ProgramData%\LarisVMS\audit-chain.dat`, da chave do selo e de onde o log começou, para que o log não possa ser selado de novo com outra chave nem apagado e reiniciado sem que isso apareça. Se você recomeçar de propósito com um banco de dados novo e vazio no mesmo servidor, exclua esse arquivo uma vez; um novo é criado com a próxima entrada.

**Encaminhar para o syslog.** Para manter uma cópia do log de auditoria fora do servidor, informe um servidor syslog em **Configurações → Logs**. Cada nova entrada é enviada conforme é escrita (RFC 5424, facility *log audit*, com a entrada em JSON). Escolha UDP, TCP ou TLS. O TLS exige que o certificado do servidor syslog seja confiável para o servidor web. Se o servidor syslog não puder ser alcançado, as entradas são mantidas e enviadas quando ele voltar. O encaminhamento começa com a próxima entrada, não com o histórico existente. Somente um Super Admin pode alterar para onde o log é encaminhado, ou desativar o encaminhamento; essa mudança é enviada antes ao destino antigo.
