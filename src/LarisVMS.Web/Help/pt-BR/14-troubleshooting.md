# Solução de problemas

Problemas comuns e onde olhar.

## Onde ficam os logs

- **Servidor**: **Logs → Logs do sistema**. Defina o nível e a retenção em **Configurações → Logs**. Debug e Trace acrescentam detalhes, mas geram arquivos grandes.
- **Log de eventos da câmera**: os eventos brutos que as câmeras enviam, usados apenas para listar os tópicos de eventos de uma câmera para as tags de evento. São mantidos por 30 dias por padrão (**Configurações → Logs**, 0 os mantém para sempre); limpá-los não toca em eventos, capturas nem gravações.
- **Manutenção do banco de dados**: toda noite, por volta das 3h, o servidor organiza os índices e as estatísticas do banco de dados e registra uma linha dizendo o que fez. Se o login dele no banco de dados não tiver permissão para isso, registra um aviso. No MariaDB isso é pulado; o MariaDB mantém as próprias estatísticas.
- **Nós**: `%ProgramData%\LarisVMS\logs\node-*.log` em cada nó.
- **Detecção de IA**: `%ProgramData%\LarisVMS\logs\vision-*.log` em cada nó.
- **Atualização automática**: `%ProgramData%\LarisVMS\logs\updater-*.log` em cada nó ou proxy.

## Uma câmera não está gravando

1. Ela está **atribuída a um nó**, e esse nó está **online**?
2. O nó tem um **caminho de armazenamento**? A lista de nós sinaliza os que não têm.
3. A câmera está **ativada**, e seus streams parecem corretos? **Sondar novamente** depois de qualquer alteração na própria câmera.
4. Nos modos Movimento, Agendamento ou Evento, verifique se as gravações estão sendo descartadas (veja [Gravação](/Help/recording#modes)).

## A visualização ao vivo e a reprodução ficam em "conectando"

Quando um nó transmite direto para os navegadores (veja [Streaming direto](/Help/nodes#direct-streaming)), o navegador se conecta à porta de cliente do nó, 4200 por padrão. Se o console do navegador mostrar uma conexão `wss://<nó>:<porta>/live/…` com falha, verifique se o firewall do nó permite essa porta: o instalador cria uma regra de entrada chamada **LarisVMS Node Client Endpoint**. Os instaladores anteriores à 0.215.1 podiam perder essa regra ao atualizar ou assumir um nó; execute o instalador de nó atual de novo, ou adicione a regra manualmente.

## O login ou a sincronização do Active Directory falha

Use **Testar conexão** em **Configurações → Active Directory**; o erro da última sincronização é mostrado na mesma página.

- **Não é possível alcançar um controlador de domínio:** verifique se o nome do domínio é resolvido a partir do servidor e se a porta 636 (LDAPS) ou 389 (LDAP) está aberta para um controlador de domínio. Com LDAPS, o controlador de domínio precisa de um certificado em que este servidor confie.
- **A identidade própria do servidor foi rejeitada:** a segurança integrada só funciona quando o serviço LarisVMS Web roda em um servidor ingressado no domínio, como LocalSystem ou uma conta de domínio. Caso contrário, use uma conta de serviço.
- **"Não está em nenhum grupo com acesso":** o usuário entrou corretamente, mas não está em um grupo vinculado. Os grupos são associados como grupos de segurança, inclusive os aninhados.
- **Bloqueado das páginas de administração depois de desativar o login local:** veja [Active Directory](/Help/users#active-directory) para o comando de banco de dados que o reativa.

## A visualização ao vivo pausa em uma janela em segundo plano

O Chrome limita janelas que não estão visíveis, então um painel ao vivo em um segundo monitor pode congelar e depois se recuperar. A visualização ao vivo se recupera sozinha. Para evitar isso, adicione estes ao destino do atalho do Chrome:

```
--disable-backgrounding-occluded-windows --disable-background-timer-throttling
```

## Todos os blocos congelam e depois reconectam de uma vez

O nó travou, não o navegador nem a rede. Procure no log do nó por `Node process health: tick … ms late`. Um nó saudável registra isso cerca de uma vez por minuto com atrasos de um dígito de milissegundos.

- Se `gcPauseMs` estiver perto do atraso, a causa é a coleta de lixo.
- Se `gcPauseMs` for pequeno mas `pendingWorkItems` estiver na casa das dezenas, há threads bloqueadas. Capture um dump enquanto trava: `dotnet-dump collect -p <pid do LarisVMS.Node>`.

## A detecção de IA para, mas a gravação continua

Geralmente o driver da GPU foi reiniciado. O log de eventos do sistema do Windows mostra o evento 153 de `nvlddmkm` ou o evento 4101 de `Display` naquele momento. O serviço de visão reinicia sozinho após 60 segundos sem resultado. Reinicializações repetidas apontam para a GPU, o driver, a refrigeração ou a energia.

## Um nó ou proxy não voltou depois de uma atualização

Inicie o serviço dele (`sc start LarisVMSNode`, ou `LarisVMSProxy`) e depois verifique o `updater-*.log` para ver por que a reinicialização falhou. Uma atualização baixada que nunca foi aplicada também é anotada no log do próprio nó ou proxy na próxima vez que ele inicia, e é oferecida de novo num check-in posterior.

## Um nó mostra "relógio"

O relógio do nó está errado em mais de um minuto em relação ao do servidor. Corrija a sincronização de horário dele. A defasagem do relógio afeta os agendamentos e a linha do tempo.

## Um nó continua falhando no check-in

Use **Redefinir autenticação** no menu **⋯** do nó. Ele limpa o token de check-in do nó, e o nó se ressincroniza no próximo heartbeat.
