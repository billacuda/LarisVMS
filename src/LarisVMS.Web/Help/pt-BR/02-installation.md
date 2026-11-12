# Instalação e atualizações

Instalar o servidor web, os nós de gravação e os proxies de mídia, e mantê-los atualizados.

Cada parte tem seu próprio instalador: `LarisVMS-Web`, `LarisVMS-Node` e `LarisVMS-Proxy` (`.msi`). Dê um clique duplo em um para ser solicitado a informar suas configurações, ou passe-as na linha de comando com `msiexec /i <arquivo>.msi NOME=valor … /qn`. O que não for informado é perguntado, e uma atualização reutiliza as configurações da instalação anterior.

## Servidor web

1. Tenha um SQL Server acessível a partir do servidor (Segurança Integrada ou Autenticação SQL), ou um servidor MariaDB (veja [MariaDB](#mariadb)).
2. Execute `LarisVMS-Web.msi`. Configurações:
   - `HTTPSPORT`: a porta HTTPS (padrão 8444). O instalador a abre no firewall, para TCP e para UDP ([HTTP/3](/Help/security#http3)).
   - `CERTPATH`, `CERTPASSWORD`: o certificado do servidor (`.pfx`). Deixe em branco para usar um certificado autoassinado até ter um. O instalador verifica se o arquivo abre com a senha antes de continuar; um compartilhamento de arquivos também deve ser legível pela conta do serviço.
   - `SERVICEACCOUNT`, `SERVICEPASSWORD`: executa o serviço como uma conta específica, por exemplo uma com direitos de Segurança Integrada do SQL. Em branco executa como LocalSystem. Desnecessário para o MariaDB.
3. Acesse `https://<host>:8444/`. O assistente de configuração guia pelo banco de dados, pela primeira conta de administrador, pelo armazenamento e pelo registro de nós. Um navegador no próprio servidor ou na rede local entra direto. De qualquer outro lugar, ele antes pede o token de configuração em `%ProgramData%\LarisVMS\setup-token.txt` no servidor (legível apenas por administradores), para que ninguém na internet configure primeiro. Para pedir o token de todos os outros computadores, rede local inclusive, defina `"Setup": { "AlwaysRequireToken": true }` em `appsettings.Production.json` antes de abrir o assistente. O arquivo é excluído depois que a conta de administrador existe.

A senha de um login SQL, ou uma senha do MariaDB, é armazenada criptografada no servidor (DPAPI, somente esta máquina), então o `setup-generated.json` não pode ser copiado para outra máquina com a senha ainda funcionando. Execute o assistente de novo lá.

Se você apontar o assistente para um banco de dados LarisVMS existente (por exemplo, depois de reinstalar o servidor), ele o usa como está: atualizado se necessário, nada é inserido ou sobrescrito e as etapas restantes são puladas. Um banco de dados que não seja do LarisVMS é recusado. Depois que a configuração termina, o assistente não pode ser aberto novamente.

O servidor não precisa de runtime .NET; ele está incluído. O certificado também pode ser alterado depois em `appsettings.Production.json` na pasta de instalação. Um `.pfx` renovado no mesmo caminho é reconhecido em até um minuto, sem reiniciar.

## MariaDB

O LarisVMS pode usar o MariaDB 10.11 ou posterior no lugar do SQL Server. No assistente de configuração, escolha **MariaDB** e informe o host, a porta (padrão 3306), o nome do banco de dados e um usuário e senha do MariaDB. O MariaDB não tem login do Windows que o LarisVMS possa usar, então um usuário e uma senha são sempre necessários.

- **O usuário** precisa de permissão para criar o banco de dados. Ou crie-o você mesmo antes, com o conjunto de caracteres `utf8mb4`, e conceda ao usuário `ALL` nele:

  ```sql
  CREATE DATABASE larisvms CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_as_ci;
  CREATE USER 'larisvms'@'%' IDENTIFIED BY 'a-strong-password';
  GRANT ALL ON larisvms.* TO 'larisvms'@'%';
  ```

- **Criptografia:** a conexão usa TLS. O certificado do servidor é verificado, a menos que o MariaDB esteja nesta máquina, ou que você marque **Confiar no certificado deste servidor** (para um certificado autoassinado em outra máquina).
- **O MariaDB anterior à 11.4 vem com o TLS desativado** até receber um certificado, e o LarisVMS não enviará a senha a outra máquina sem ele. Adicione o certificado e sua chave à seção `[mysqld]` do `my.ini`, reinicie o MariaDB e marque **Confiar no certificado deste servidor** se ele for autoassinado:

  ```ini
  [mysqld]
  ssl_cert = C:/MariaDB/certs/server-cert.pem
  ssl_key = C:/MariaDB/certs/server-key.pem
  ```

  O MariaDB 11.4 e posteriores configuram um certificado sozinhos.
- **Os backups** precisam das ferramentas cliente do MariaDB no servidor do LarisVMS (veja [Backups](/Help/backups)).
- Uma instalação existente do SQL Server não pode ser migrada para o MariaDB; o MariaDB é para instalações novas.

## Nós

Execute `LarisVMS-Node.msi` em cada máquina de gravação. Ele precisa de:

- `SERVERURL` e `REGISTRATIONKEY`: o endereço do servidor e a **chave de registro** em **Nós → Atualizações e registro**. São usados apenas na primeira vez que o nó se registra. Mudar a chave não afeta nós existentes.
- **FFmpeg**, que não vem incluído. Instale-o antes com `winget install ffmpeg --scope machine`. O instalador o encontra, ou defina `FFMPEGPATH`.
- `STORAGEROOT`: onde o nó grava. Também pode ser definido depois na página de edição do nó.

Opcionais: `ARCHIVEROOT`, `LIVEPORT` (padrão 8554), `LIVEALLOWFROM` (endereços permitidos pelo firewall até a porta ao vivo, como IPs ou blocos CIDR separados por vírgula: o servidor LarisVMS e eventuais proxies; em branco permite qualquer um), `INSECURETLS=1` (não é mais necessário: os nós confiam no certificado autoassinado do servidor no primeiro uso, veja [Certificado do servidor](/Help/nodes#server-certificate)), o endpoint de streaming direto (`CLIENTPORT`, `CLIENTENDPOINTHOST`, `CLIENTPFXPATH`, `CLIENTPFXPASSWORD`) e `SERVICEACCOUNT` / `SERVICEPASSWORD` para uma conta que alcance armazenamento SMB.

Para a detecção de IA, as máquinas NVIDIA também precisam do CUDA Toolkit 12.x e do cuDNN 9.x; o instalador copia as DLLs deles para o lugar quando as encontra. Veja [Detecção de IA](/Help/ai-detection#hardware).

Depois de instalado, o nó aparece em **Nós** como **aguardando aprovação**. Ele não grava nada e não recebe câmeras até você clicar em **Aprovar**, então aprove apenas um nó que você mesmo instalou. Depois atribua câmeras a ele pela página de edição de cada câmera.

## Proxies de mídia

Execute `LarisVMS-Proxy.msi` com `SERVERURL`, `REGISTRATIONKEY` e `CLIENTPORT` (padrão 4443), além das mesmas configurações opcionais de certificado de um nó. Um novo proxy começa desativado: ative **Ativado** para ele em **Configurações → Proxies de mídia** e depois escolha-o na página de cada nó (veja [Nós](/Help/nodes#proxies)).

## Atualizações

**Atualizando:** execute o `.msi` mais novo. Ele encontra a instalação existente e vai direto para **Pronto para atualizar**; as configurações são lembradas, então nada precisa ser digitado de novo, exceto a senha de uma conta de serviço de domínio. O servidor web aplica sozinho quaisquer mudanças do banco de dados ao iniciar. As atualizações nunca tocam em gravações, arquivos de configuração, registro de nós nem chaves de proteção de dados, e uma instalação com falha é revertida.

**Nós e proxies se atualizam sozinhos.** Uma nova instalação web registra as builds de nó e de proxy que traz como **Pendentes** na seção **Builds** de **Nós**. Apenas a build de nó mais recente e a build de proxy mais recente esperam aprovação; as mais antigas ficam **Substituídas**. **Histórico de builds** lista todas as builds. Aprove uma, e todo nó mais antigo a baixa, verifica e instala no próximo check-in. O servidor assina toda atualização com sua chave de assinatura (veja **Configurações → Criptografia e assinatura**); nós e proxies guardam essa chave desde que se registraram e não instalam nada que não esteja assinado com ela, mesmo que a conexão deles com o servidor fosse adulterada. Desative isto com **Atualização automática** em **Nós → Atualizações e registro**.

**Novas versões:** uma vez por dia (e quando o serviço inicia) o servidor verifica as releases do LarisVMS no GitHub. Quando há uma versão mais nova, quem pode alterar configurações vê **✨ A versão X está disponível** na barra superior, com um link para as notas de lançamento. Nada sobre a sua instalação é enviado. Desative com **Novas versões** em **Nós → Atualizações e registro**.

**Desinstalar** remove o programa, seu serviço e as regras de firewall. Gravações, configuração e `%ProgramData%\LarisVMS` são mantidos.

## Instalando a partir do código-fonte

Desenvolvedores podem compilar e instalar a partir do repositório com o PowerShell: `install-web.ps1` (servidor) e os scripts `install-node.ps1` / `install-proxy.ps1` nos pacotes gerados por `build-node.ps1` / `build-proxy.ps1`. O `build-installers.ps1` gera os três MSIs. Um MSI atualiza no lugar uma máquina instalada por script, mantendo suas configurações, seu registro e qualquer endpoint de streaming direto com sua regra de firewall.

## Certificados

`Kestrel:Certificates:Default:Path` e `:Password` em `appsettings.Production.json` apontam para um arquivo `.pfx`. O servidor o verifica a cada 60 segundos e passa a usar um certificado renovado sem reiniciar.
