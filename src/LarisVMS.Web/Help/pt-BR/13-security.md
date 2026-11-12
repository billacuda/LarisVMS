# Segurança

Listas de IPs permitidos, uma porta separada para ao vivo/reprodução, a política de scripts rígida, certificados, nomes de host, atualizações, segredos criptografados e proteção extra para empresas.

## Listas de permissão

**Configurações → Segurança** tem duas listas de IPs permitidos. Informe um IP ou bloco CIDR por linha (por exemplo `10.0.0.5` ou `10.0.0.0/24`); as linhas que começam com `#` são comentários. Uma lista em branco permite todo mundo.

- **Gerenciamento / API**: as páginas de administração e a API REST. Uma lista que bloquearia o endereço de onde você está salvando é recusada, então você não consegue se trancar para fora.
- **Visualização ao vivo / reprodução**: vídeo ao vivo, reprodução, miniaturas, downloads de exportação e capturas de câmeras.

## Porta separada

**Configurações → Visualização ao vivo → Porta personalizada** move o tráfego ao vivo e de reprodução para uma porta HTTPS própria, usando o mesmo certificado. Isso permite protegê-la separadamente com firewall, ou expor só ela fora da LAN. Tem efeito depois de `Restart-Service LarisVMSWeb`. A partir daí, o vídeo só responde na nova porta e todo o resto só na porta principal. Esvazie o campo para voltar a uma porta só.

## HTTP/3

O servidor usa HTTP/2 e acrescenta HTTP/3 no Windows 11, Windows Server 2022 ou posterior. Os navegadores começam em HTTP/2 e passam para HTTP/3 sozinhos quando conseguem alcançá-lo. O HTTP/3 usa UDP na porta HTTPS (e na porta personalizada, se definida). O instalador abre TCP e UDP; numa instalação mais antiga, adicione uma regra UDP para a porta ou execute o instalador atual de novo. Até lá, tudo continua funcionando por HTTP/2. Para desativar o HTTP/3, desmarque **Configurações → Segurança → HTTP/3** e depois `Restart-Service LarisVMSWeb`.

## Política de scripts rígida

Com **Configurações → Segurança → Política de scripts rígida** ativada, os navegadores executam apenas os scripts que este servidor envia, então um script injetado em uma página não pode rodar. Ela vem ativada por padrão, e atualizar a ativa. Se algo parar de funcionar, desative-a: os navegadores então informam tudo o que a política rígida teria bloqueado, e os relatórios mais recentes aparecem abaixo da chave. Quando nenhum relatório aparecer depois de você usar as páginas de que depende, ative-a de novo. Uma alteração tem efeito no próximo carregamento de página.

## Certificados

O certificado do servidor é um arquivo `.pfx` definido em `appsettings.Production.json`. Um arquivo renovado é reconhecido em até um minuto, sem reiniciar. Os nós que usam streaming direto têm sua própria configuração de certificado na página do nó.

## Certificados de câmeras

As câmeras acessadas por HTTPS têm certificados autoassinados que não podem ser verificados contra nada, então por padrão qualquer certificado é aceito. Ative **Configurações → Segurança → Fixar certificados das câmeras** para que o servidor lembre o certificado de cada endereço de câmera na primeira vez que se conecta, e recuse um diferente a partir daí, no servidor e nos nós. Algo na rede se passando por uma câmera então não consegue coletar a senha dela nem enviar eventos falsos.

Os certificados lembrados são listados abaixo da configuração. Se o certificado de uma câmera realmente mudar (uma atualização de firmware, uma redefinição ou um novo certificado), selecione **Esquecer** ao lado do endereço dela; o próximo certificado que ela apresentar é lembrado. Até lá, cada conexão recusada, no servidor ou em um nó, é registrada no log de auditoria e os Super Admins são avisados (no máximo uma vez por hora por endereço de câmera), pois algo na rede pode estar se passando pela câmera. Um endereço de câmera é lembrado quando o servidor o alcança pela primeira vez, normalmente quando a câmera é adicionada; um nó aceita um endereço que o servidor ainda não alcançou.

## Nomes de host

Por padrão o servidor responde a qualquer nome de host que um navegador use, então funciona por endereço IP, nome curto e nome completo. Para responder apenas aos nomes que você usa, defina `AllowedHosts` em `appsettings.Production.json` com eles, separados por ponto e vírgula (por exemplo `"AllowedHosts": "vms.example.com;vms;localhost"`), e depois `Restart-Service LarisVMSWeb`. Inclua todos os nomes que nós, proxies e pessoas usam para alcançá-lo: qualquer outro gera um erro.

Atrás de um proxy reverso (IIS ARR, nginx e assim por diante), liste o endereço dele em `appsettings.Production.json` como `"ReverseProxy": { "KnownProxies": [ "10.0.0.2" ] }` e reinicie. O servidor então pega o endereço e o esquema reais do cliente dos cabeçalhos `X-Forwarded-For` e `X-Forwarded-Proto` desse proxy, de modo que as listas de permissão e o log de auditoria vejam a pessoa, não o proxy. Esses cabeçalhos são ignorados quando vêm de qualquer coisa que não esteja na lista, e totalmente enquanto a lista estiver vazia.

## Atualizações

Estas verificações ficam entre um download e uma instalação em execução:

- **Os downloads de releases** vêm com o `SHA256SUMS.txt`. Confira um instalador antes de executá-lo com `Get-FileHash <arquivo> -Algorithm SHA256` e compare com a linha daquele arquivo, ou execute `sha256sum -c SHA256SUMS.txt`. Quando os instaladores são assinados, o Windows também mostra o editor.
- **As atualizações de nós e proxies** são assinadas pela chave de assinatura deste servidor. Um nó ou proxy guarda essa chave desde que se registrou e não instala nada que não esteja assinado com ela, mesmo que a conexão dele com o servidor fosse adulterada. Ele também recusa uma build que não seja mais nova que a que executa. Veja [Instalação](/Help/installation#updates).
- **A assinatura do editor** (opcional): um nó ou proxy compilado com a chave do editor (`-PublisherKeyPath`) também exige a assinatura do editor em cada atualização; as outras builds pulam essa verificação. Essa chave é mantida offline e nunca no servidor, então nem um servidor invadido consegue empurrar seu próprio código para os nós.
- **A conexão** com o servidor é verificada conforme definido em **Nós → Atualizações e registro → Verificação do certificado do servidor** (veja [Nós](/Help/nodes#server-certificate)).

## Segredos criptografados

As credenciais das câmeras, as credenciais SMB, as chaves dos nós, os segredos de e-mail, o segredo do cliente do Entra e a senha da conta de serviço do Active Directory são criptografados no banco de dados, usando as chaves de proteção de dados em `%ProgramData%\LarisVMS\keys`. Somente SYSTEM, Administrators e as contas dos serviços do LarisVMS podem abrir `%ProgramData%\LarisVMS`: qualquer outro acesso encontrado ali, como uma pasta criada por um usuário local antes de o LarisVMS ser instalado, é removido quando um serviço inicia. Faça backup dessa pasta e nunca a exclua durante um deploy.

## Proteção extra para empresas

De fábrica, o LarisVMS é configurado para que uma instalação doméstica simplesmente funcione. Tudo abaixo é opcional e vem desativado por padrão; ative o que combina com uma empresa, ou com um servidor acessível pela internet:

- **Exigir login em duas etapas**: Configurações → Segurança (veja [Login em duas etapas](/Help/users#two-factor-sign-in)).
- **Confirmar a senha para páginas de administração**: Configurações → Segurança (veja [Sessões](/Help/users#sessions)).
- **Senhas temporárias definidas pelo administrador**: Configurações → Segurança (veja [Senhas](/Help/users#passwords)).
- **Sessões mais curtas e um tempo limite de inatividade**: cada função em Configurações → Funções (veja [Funções](/Help/users#roles)).
- **Fixar certificados das câmeras**: Configurações → Segurança (veja [Certificados de câmeras](#camera-certificates)).
- **Listas de permissão**: Configurações → Segurança (veja [Listas de permissão](#allow-lists)).
- **O token de configuração de todos os outros computadores**: defina `"Setup": { "AlwaysRequireToken": true }` em `appsettings.Production.json` antes de executar o assistente de configuração (veja [Instalação](/Help/installation#web-server)).
- **Fixar o certificado do servidor ao instalar um nó ou proxy**: `-ServerCertThumbprint` (veja [Certificado do servidor](/Help/nodes#server-certificate)).
- **Códigos de registro de uso único, com a chave compartilhada desativada**: Nós → Atualizações e registro (veja [Códigos de registro de uso único](/Help/nodes#one-time-registration-codes)).
- **Conexões criptografadas do servidor para os nós**: na página de cada nó (veja [Conexões criptografadas do servidor](/Help/nodes#encrypted-connections-from-the-server)).
- **Encaminhar o log de auditoria para o syslog**: Configurações → Logs (veja [Log de auditoria](/Help/users#audit-log)).
- **Atualizações assinadas pelo editor**, para builds que você mesmo gera (veja [Atualizações](#updates)).
- **Uma conta de serviço menos privilegiada.** Os serviços do LarisVMS rodam como LocalSystem. Você pode executá-los com outra conta (`services.msc`, ou `sc.exe config`), mas então cabe a você dar a essa conta o que o serviço precisa: a pasta de instalação, cada pasta ou compartilhamento de armazenamento e de arquivo morto, e os arquivos de certificado. `%ProgramData%\LarisVMS` é tratado por você: cada serviço do LarisVMS, ao iniciar, o limita a SYSTEM, Administrators e às contas com que os serviços do LarisVMS rodam, e remove qualquer outro acesso ali. Confira o log do serviço depois da mudança.
