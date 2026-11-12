# Backups

Backups do banco de dados e o que mais precisa de backup.

**Configurações → Backups** faz backups completos do SQL Server do banco de dados do LarisVMS, em um agendamento diário ou sob demanda. É pensado para o SQL Express, que não tem SQL Agent para agendar seus próprios backups.

- **Diretório de backup**: uma pasta na qual o *serviço do SQL Server* possa gravar, fora da pasta de instalação do LarisVMS. Se o SQL Server estiver em outra máquina, esse caminho precisa existir nela, e os backups antigos não são limpos automaticamente.
- **Manter os últimos**: quantos backups manter (somente SQL Server local).

A restauração é feita fora do LarisVMS, com o SSMS ou o `sqlcmd`. Os backups são apenas completos. No SQL Express, use o modelo de recuperação SIMPLE.

## MariaDB

No MariaDB, os backups são feitos com o `mariadb-dump` e salvos como arquivos compactados `.sql.gz`. O `mariadb-dump` roda no servidor do LarisVMS, então as ferramentas cliente do MariaDB precisam estar instaladas ali, mesmo quando o próprio MariaDB roda em outra máquina. O LarisVMS as encontra em `C:\Program Files\MariaDB …`, ou um Super Admin pode definir o caminho do `mariadb-dump.exe` na página Backups. Esse caminho precisa estar neste servidor, em uma pasta que só administradores possam alterar (como em Arquivos de Programas), pois o LarisVMS o executa com os direitos do próprio serviço.

- **Diretório de backup**: uma pasta no servidor do LarisVMS (ou um compartilhamento) na qual o *serviço do LarisVMS* possa gravar, fora da pasta de instalação. Os backups antigos são sempre limpos, onde quer que o MariaDB rode.
- O backup contém o banco de dados inteiro em um único instantâneo consistente, feito sem interromper a gravação.

Descompacte o `.sql.gz` (com o 7-Zip, por exemplo) e depois restaure-o com o cliente `mariadb`. O arquivo cria o banco de dados se ele não existir e substitui suas tabelas, então pare antes o serviço LarisVMS Web:

```powershell
cmd /c "mariadb -u root -p < larisvms_20261026_030000.sql"
```

## Faça backup também de

- **As chaves de proteção de dados** em `%ProgramData%\LarisVMS\keys`. As senhas das câmeras, os segredos de e-mail e as chaves dos nós são criptografados com elas. Sem essas chaves, um banco de dados restaurado não consegue descriptografar nenhum deles.
- **`setup-generated.json`** na pasta de instalação, que guarda a cadeia de conexão com o banco de dados. A senha dela só funciona na máquina que a salvou; em uma máquina nova, execute o assistente de configuração de novo e aponte-o para o banco de dados restaurado.

As gravações não fazem parte do backup do banco de dados. Proteja-as com o volume de arquivo morto ou com os seus próprios backups de armazenamento.
