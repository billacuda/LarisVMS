# Instalação e atualizações

Instalar o servidor web, os nós de gravação e os proxies de mídia, e mantê-los atualizados.

Cada parte tem seu próprio instalador: `LarisVMS-Web`, `LarisVMS-Node` e `LarisVMS-Proxy` (`.msi`). Dê um clique duplo em um para ser solicitado a informar suas configurações, ou passe-as na linha de comando com `msiexec /i <arquivo>.msi NOME=valor … /qn`. O que não for informado é perguntado, e uma atualização reutiliza as configurações da instalação anterior.

Cada parte também funciona no Linux, a partir de um pacote `.deb` ou `.rpm` (veja [Linux](#linux)), e os dois podem ser misturados: um servidor Windows pode ter nós e proxies Linux, e vice-versa.

## Servidor web

1. Tenha um SQL Server acessível a partir do servidor (Segurança Integrada ou Autenticação SQL), ou um servidor MariaDB (veja [MariaDB](#mariadb)).
2. Execute `LarisVMS-Web.msi`. Configurações:
   - `HTTPSPORT`: a porta HTTPS (padrão 8444). O instalador a abre no firewall, para TCP e para UDP ([HTTP/3](/Help/security#http3)).
   - `CERTPATH`, `CERTPASSWORD`: o certificado do servidor (`.pfx`). Deixe em branco para usar um certificado autoassinado até ter um. O instalador verifica se o arquivo abre com a senha antes de continuar; um compartilhamento de arquivos também deve ser legível pela conta do serviço.
   - `SERVICEACCOUNT`, `SERVICEPASSWORD`: executa o serviço como uma conta específica, por exemplo uma com direitos de Segurança Integrada do SQL. Em branco executa como LocalSystem. Desnecessário para o MariaDB.
3. Acesse `https://<host>:8444/`. O assistente de configuração guia pelo banco de dados, pela primeira conta de administrador, pelo armazenamento e pelo registro de nós. Um navegador no próprio servidor ou na rede local entra direto. De qualquer outro lugar, ele antes pede o token de configuração em `%ProgramData%\LarisVMS\setup-token.txt` no servidor (`/var/lib/larisvms/setup-token.txt` no Linux; legível apenas por administradores), para que ninguém na internet configure primeiro. Para pedir o token de todos os outros computadores, rede local inclusive, defina `"Setup": { "AlwaysRequireToken": true }` em `appsettings.Production.json` antes de abrir o assistente. O arquivo é excluído depois que a conta de administrador existe.

A senha de um login SQL, ou uma senha do MariaDB, é armazenada criptografada no servidor (DPAPI no Windows, ou uma chave vinculada ao ID da máquina no Linux; somente esta máquina), então o `setup-generated.json` não pode ser copiado para outra máquina com a senha ainda funcionando. Execute o assistente de novo lá.

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

Para a detecção de IA, as máquinas NVIDIA também precisam do CUDA 12 e do cuDNN 9; no Windows, o instalador copia as DLLs deles para o lugar quando as encontra. Veja [Detecção de IA](/Help/ai-detection#hardware).

Depois de instalado, o nó aparece em **Nós** como **aguardando aprovação**. Ele não grava nada e não recebe câmeras até você clicar em **Aprovar**, então aprove apenas um nó que você mesmo instalou. Depois atribua câmeras a ele pela página de edição de cada câmera.

## Proxies de mídia

Execute `LarisVMS-Proxy.msi` com `SERVERURL`, `REGISTRATIONKEY` e `CLIENTPORT` (padrão 4443), além das mesmas configurações opcionais de certificado de um nó. Um novo proxy começa desativado: ative **Ativado** para ele em **Configurações → Proxies de mídia** e depois escolha-o na página de cada nó (veja [Nós](/Help/nodes#proxies)).

## Linux

O servidor web, os nós e os proxies são instalados a partir de pacotes `.deb` (Ubuntu 22.04, 24.04, 26.04, Debian 12, 13) ou `.rpm` (Rocky Linux 9, 10 e outras derivadas do RHEL 9 ou 10), para x86_64. Cada um roda como um serviço do systemd (`larisvms-web`, `larisvms-node`, `larisvms-proxy`) com sua própria conta `larisvms`. O programa fica em `/opt/larisvms`, as configurações em `/etc/larisvms`, os dados (chaves, modelos, o token de configuração) em `/var/lib/larisvms` e os arquivos de log em `/var/log/larisvms`. Não é preciso o runtime do .NET. A instalação abre as portas do serviço no firewalld ou no ufw quando um deles está ativo.

- **Servidor web:** `sudo apt install ./larisvms-web_<version>-1_amd64.deb` (ou `sudo dnf install ./larisvms-web-<version>-1.x86_64.rpm`). Ele inicia em seguida; abra `https://<host>:8444/` para o assistente de configuração. A porta e o certificado são definidos em `/etc/larisvms/web.env` (`Kestrel__HttpsPort`, `Kestrel__Certificates__Default__Path`, `Kestrel__Certificates__Default__Password`; a conta larisvms precisa conseguir ler o arquivo), e depois `sudo systemctl restart larisvms-web`. `appsettings.Production.json` e `setup-generated.json` também ficam em `/etc/larisvms`. O SQL Server precisa de um login SQL no Linux; lá o MariaDB é a escolha mais simples.
- **Nós:** instale o `larisvms-node` e depois associe-o com `sudo larisvms-node setup --server-url https://<server>:8444 --registration-key <key>`. O passo Nó do assistente de configuração mostra esse comando com a chave e a impressão digital do certificado do servidor já preenchidas. Ele grava em `/var/lib/larisvms/recordings`, a menos que você acrescente `--storage-root <folder>`; um compartilhamento de rede precisa ser montado antes (por exemplo, em `/etc/fstab`) e permitir escrita pela conta larisvms. Outras opções: `--archive-root`, `--live-port`, `--live-allow-from`, `--client-port`, `--client-pfx-path`, `--client-endpoint-host` e `--ffmpeg-path`. `sudo larisvms-node setup --help` lista todas, e executar `setup` de novo as altera.
- O **ffmpeg** vem da distribuição e é instalado com o nó. No Rocky Linux ele vem do RPM Fusion, então ative EPEL, CRB e RPM Fusion antes de instalar um nó.
- **Proxies:** instale o `larisvms-proxy` e depois `sudo larisvms-proxy setup --server-url https://<server>:8444 --registration-key <key> --client-port 4443`.

Verifique um serviço com `systemctl status larisvms-node` e `journalctl -u larisvms-node`, e mostre a versão com `larisvms-node --version`.

**Docker:** toda versão também é publicada como imagens no registro de contêineres do GitHub: `ghcr.io/billacuda/larisvms-web`, `larisvms-node` (com as tags `-cuda` e `-tensorrt` para GPUs NVIDIA) e `larisvms-proxy`. O servidor web precisa de um banco de dados mantido por você (MariaDB 10.11 ou posterior, ou SQL Server com um login SQL). Instale-o de uma destas formas:

- **Docker Compose:** baixe o [compose.yml](https://github.com/billacuda/LarisVMS/blob/main/docker/compose.yml) e execute `docker compose up -d` na pasta dele. Isso inicia o servidor web; abra `https://<host>:8444/` para o assistente de configuração. Depois remova os comentários do nó (e do proxy, se quiser um) no arquivo, preencha o endereço do servidor, a chave de registro e a pasta de gravações, e execute `docker compose up -d` de novo.
- **Stack do Portainer:** em **Stacks → Add stack → Web editor**, cole o mesmo arquivo e escolha **Deploy the stack**. Depois do assistente, remova os comentários do nó na aba **Editor** do stack e escolha **Update the stack**. Use um ambiente Docker autônomo, não um Swarm.
- **Docker puro:** inicie o servidor web, execute o assistente e depois inicie o nó:

```bash
docker run -d --name larisvms-web --network host --restart unless-stopped \
  -e Kestrel__HttpsPort=8444 \
  -v /srv/larisvms/web/data:/var/lib/larisvms \
  -v /srv/larisvms/web/config:/etc/larisvms \
  -v /srv/larisvms/web/logs:/var/log/larisvms \
  ghcr.io/billacuda/larisvms-web:latest

docker run -d --name larisvms-node --hostname larisvms-node-1 --network host --restart unless-stopped \
  -e LARISVMS_SERVER_URL=https://<server address>:8444 \
  -e LARISVMS_REGISTRATION_KEY=<key> \
  -e LARISVMS_STORAGE_ROOT=/recordings \
  -v /srv/larisvms/node/data:/var/lib/larisvms \
  -v /srv/larisvms/node/config:/etc/larisvms \
  -v /srv/larisvms/node/logs:/var/log/larisvms \
  -v /srv/larisvms/recordings:/recordings \
  ghcr.io/billacuda/larisvms-node:latest
```

Em seguida, aprove o nó em **Nós**. Como endereço do servidor, use o endereço ou nome dele na LAN, não `localhost`, mesmo na mesma máquina. Se a máquina tiver firewall, permita TCP 8444 (servidor web), 8554 (nó), 8555 (um nó que transmite direto para os navegadores) e 4443 (proxy), e UDP 8444 se usar HTTP/3. Os contêineres rodam como usuário e grupo 8444, dono das pastas deles no host.

O passo Nó do assistente de configuração e a página **Proxies de mídia** mostram um stack do Compose para o Portainer ou o `docker compose` com o endereço, a chave de registro e o certificado deste servidor já preenchidos. As configurações são as mesmas variáveis de ambiente usadas pelos serviços do Linux (`LARISVMS_SERVER_URL`, `LARISVMS_REGISTRATION_KEY`, `LARISVMS_STORAGE_ROOT` e assim por diante). Mantenha `/var/lib/larisvms` em um volume: ele guarda as chaves, o registro e o ID da máquina que mantém as senhas salvas legíveis quando o contêiner é recriado. Use a rede do host, para que a descoberta de câmeras funcione. Para atualizar, execute `docker compose pull` e `docker compose up -d`, escolha **Update the stack** com **Re-pull image and redeploy** ativado no Portainer ou, com Docker puro, baixe a imagem, remova o contêiner e execute o comando dele de novo.

**Mover uma instalação Linux para o Docker:** copie a pasta de dados (principalmente `keys`) e o ID da máquina antes do primeiro início do contêiner, e faça antes um backup do banco de dados. A senha do banco de dados e os registros dos nós são criptografados com o ID da máquina, e as senhas e chaves de nó no banco de dados com `keys`; sem eles, páginas como **Nós** mostram um erro. Uma instalação Windows não pode ser movida assim. Para o servidor web:

```bash
sudo systemctl disable --now larisvms-web
sudo mkdir -p /srv/larisvms/web/data /srv/larisvms/web/config
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./node.config --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/web/data -xf -
sudo cp -a /etc/larisvms/. /srv/larisvms/web/config/
sudo install -m 600 /etc/machine-id /srv/larisvms/web/data/machine-id
```

Passe as configurações de `/etc/larisvms/web.env` para o `environment` do stack e inicie o contêiner. Ele se conecta ao mesmo banco de dados, sem o assistente. Nunca execute o serviço antigo e o contêiner ao mesmo tempo. Para um nó, deixe as gravações onde estão:

```bash
sudo systemctl disable --now larisvms-node
sudo mkdir -p /srv/larisvms/node/data
sudo tar -C /var/lib/larisvms --exclude=./recordings --exclude=./keys --exclude=./update --exclude=./proxy.config -cf - . \
  | sudo tar -C /srv/larisvms/node/data -xf -
sudo install -m 600 /etc/machine-id /srv/larisvms/node/data/machine-id

# The container's account needs to own the recordings (repeat for an archive folder):
sudo chown -R 8444:8444 /var/lib/larisvms/recordings
```

Passe as configurações de `/etc/larisvms/node.env` para o `environment` do stack e monte cada pasta de gravações e de arquivo morto no mesmo caminho dentro do contêiner (por exemplo, `/var/lib/larisvms/recordings:/var/lib/larisvms/recordings`, com `LARISVMS_STORAGE_ROOT` apontando para ele). O nó volta como ele mesmo, com suas câmeras e gravações, sem precisar de chave de registro nem de aprovação. Um proxy é movido da mesma forma, sem as gravações. Remover depois os pacotes antigos fecha as portas no firewall, então abra-as de novo para os contêineres.

Se você já executou o assistente no contêiner com o banco de dados antigo, só faltam as chaves. Copie-as para junto das chaves do contêiner e mantenha ambas:

```bash
docker stop larisvms-web
sudo docker cp /var/lib/larisvms/keys/. larisvms-web:/var/lib/larisvms/keys/
docker start larisvms-web
```

Onde a Ajuda cita uma pasta do Windows, a do Linux é: `%ProgramData%\LarisVMS` → `/var/lib/larisvms` (a pasta `logs` → `/var/log/larisvms`), e `appsettings.Production.json` e `setup-generated.json` da pasta de instalação → `/etc/larisvms`. `Restart-Service LarisVMSWeb` é `sudo systemctl restart larisvms-web`.

## Atualizações

**Atualizando:** execute o `.msi` mais novo. Ele encontra a instalação existente e vai direto para **Pronto para atualizar**; as configurações são lembradas, então nada precisa ser digitado de novo, exceto a senha de uma conta de serviço de domínio. O servidor web aplica sozinho quaisquer mudanças do banco de dados ao iniciar. As atualizações nunca tocam em gravações, arquivos de configuração, registro de nós nem chaves de proteção de dados, e uma instalação com falha é revertida.

**Nós e proxies se atualizam sozinhos.** Uma nova instalação web registra as builds de nó e de proxy que traz como **Pendentes** na seção **Builds** de **Nós**. Apenas a build de nó mais recente e a build de proxy mais recente esperam aprovação; as mais antigas ficam **Substituídas**. **Histórico de builds** lista todas as builds. Aprove uma, e todo nó mais antigo a baixa, verifica e instala no próximo check-in. O servidor assina toda atualização com sua chave de assinatura (veja **Configurações → Criptografia e assinatura**); nós e proxies guardam essa chave desde que se registraram e não instalam nada que não esteja assinado com ela, mesmo que a conexão deles com o servidor fosse adulterada. Desative isto com **Atualização automática** em **Nós → Atualizações e registro**.

**Novas versões:** uma vez por dia (e quando o serviço inicia) o servidor verifica as releases do LarisVMS no GitHub. Quando há uma versão mais nova, quem pode alterar configurações vê **✨ A versão X está disponível** na barra superior, com um link para as notas de lançamento. Nada sobre a sua instalação é enviado. Desative com **Novas versões** em **Nós → Atualizações e registro**.

**No Linux,** atualize o servidor web instalando o pacote mais novo da mesma forma; ele mantém `/etc/larisvms` e `/var/lib/larisvms`. Os nós e proxies Linux se atualizam sozinhos pelo servidor, como os do Windows, depois que você aprova a build: um pequeno auxiliar que roda como root instala a atualização que o serviço baixou e verificou. Um servidor Linux oferece aos nós Windows a build do Windows, e um servidor Windows oferece aos nós Linux a deles.

**Desinstalar** remove o programa, seu serviço e as regras de firewall. Gravações, configuração e `%ProgramData%\LarisVMS` são mantidos. No Linux, `apt remove` ou `dnf remove` faz o mesmo, mantendo `/var/lib/larisvms`, `/var/log/larisvms` e `/etc/larisvms`.

## Instalando a partir do código-fonte

Desenvolvedores podem compilar e instalar a partir do repositório com o PowerShell: `install-web.ps1` (servidor) e os scripts `install-node.ps1` / `install-proxy.ps1` nos pacotes gerados por `build-node.ps1` / `build-proxy.ps1`. O `build-installers.ps1` gera os três MSIs e os pacotes Linux `.deb` e `.rpm` (com nFPM). Um MSI atualiza no lugar uma máquina instalada por script, mantendo suas configurações, seu registro e qualquer endpoint de streaming direto com sua regra de firewall.

## Certificados

`Kestrel:Certificates:Default:Path` e `:Password` em `appsettings.Production.json` apontam para um arquivo `.pfx` (no Linux, também configurável em `/etc/larisvms/web.env` como `Kestrel__Certificates__Default__Path` e `__Password`). O servidor o verifica a cada 60 segundos e passa a usar um certificado renovado sem reiniciar.

No Docker, monte a pasta do certificado (`/srv/larisvms/certs:/certs:ro`) e use o caminho dentro do contêiner, como `/certs/vms.pfx`. O usuário 8444 precisa conseguir lê-lo, e um `$` na senha é escrito `$$` em um arquivo do Compose. Um certificado que não pode ser carregado é substituído por um autoassinado, e o `docker logs` diz por quê.
