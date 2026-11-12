# Idiomas

Mostrar o LarisVMS em outro idioma e adicionar sua própria tradução.

## Escolhendo um idioma

Escolha um idioma no menu 🌐 no alto de qualquer página, inclusive a de login. Sua escolha o acompanha em todo dispositivo em que você entrar. O menu só aparece quando mais de um idioma está instalado.

Um administrador define em **Configurações → Idioma** o idioma de quem não escolheu um. Deixe em **O idioma do navegador** para seguir o navegador de cada pessoa.

Datas, horas e números também seguem o idioma. Se o seu navegador usa o mesmo idioma com um formato regional (inglês britânico, por exemplo), o formato regional é mantido.

Os alertas e as notificações chegam a cada pessoa no idioma dela. Os canais da equipe (e-mail para um endereço fixo, webhook, ntfy, Pushover, Slack e Teams) usam o idioma padrão. O log de auditoria permanece em inglês.

O texto que um idioma ainda não traduziu aparece em inglês.

## Adicionando um idioma

Não é preciso programar: um idioma é um arquivo de texto.

1. Em **Configurações → Idioma**, baixe o **en.json**, o texto em inglês.
2. Traduza o texto à direita de cada linha. Mantenha a chave à esquerda exatamente como está.
3. Envie o arquivo em **Configurações → Idioma**, ou copie-o para `%ProgramData%\LarisVMS\locales` no servidor. Dê a ele o nome do código do idioma, por exemplo `fr.json`, `de.json` ou `pt-BR.json`.

Ele fica disponível na hora, sem reiniciar. Um arquivo que não puder ser lido é listado na página Idioma com o motivo, e o resto do LarisVMS continua funcionando.

### O que manter como está

- **Tudo entre {chaves}** é preenchido pelo LarisVMS, como `{camera}` ou `{count}`. Mova-o para onde ele fizer sentido na sua frase, mas não o traduza nem o renomeie.
- **Algumas tags** podem aparecer no texto: `<code>`, `<strong>`, `<em>`, `<b>`, `<i>`, `<kbd>`, `<br>` e `<a href="…">`. Mantenha-as em volta das mesmas palavras. Outro HTML não é permitido e aparece como texto simples.
- A seção **_meta** no alto contém o nome do idioma como aparece no menu 🌐 (por exemplo "Français") e o seu nome, se você quiser crédito.

### Plurais

Alguns textos mudam com um número. Em inglês há duas formas:

```json
"cameras.count_one": "{count} camera",
"cameras.count_other": "{count} cameras"
```

Adicione as formas que o seu idioma precisa, com estas terminações: `_zero`, `_one`, `_two`, `_few`, `_many` e `_other`. O russo, por exemplo, usa `_one`, `_few` e `_many`. Inclua sempre `_other`; ela é usada quando nenhuma outra forma se aplica.

### Páginas de ajuda

As páginas de ajuda são traduzidas um arquivo por vez. Cada página mantém o nome do arquivo em inglês, então o endereço dela não muda, e as páginas que você não traduziu permanecem em inglês. Um idioma regional (`es-AR`) usa primeiro as próprias páginas, depois as do geral (`es`), depois o inglês.

- **Para o seu próprio servidor:** copie o Markdown de uma página para `locales\<código>\help\` com o mesmo nome de arquivo (por exemplo `locales\fr\help\03-cameras.md`) e traduza.
- **Para contribuir com o LarisVMS:** veja o [CONTRIBUTING.md](https://github.com/billacuda/LarisVMS/blob/main/CONTRIBUTING.md) para saber como enviar. As páginas traduzidas ficam em `src/LarisVMS.Web/Help/<código>/` no código-fonte (por exemplo `Help/es-AR/03-cameras.md`), ao lado das páginas em inglês em `Help/en/`. As páginas contribuídas acompanham o LarisVMS e também aparecem em [larisvms.com/docs](https://www.larisvms.com/docs/), onde os visitantes podem escolher o idioma.

A Ajuda aparece em um idioma quando esse idioma está instalado (o arquivo `.json` dele). O HTML em uma página é mostrado como texto simples, então use Markdown para formatar.

### Atualizando uma tradução

As novas versões do LarisVMS acrescentam texto. A coluna **Traduzido** na página Idioma mostra quanto do texto atual cada idioma cobre, e **Páginas de ajuda** para quantas páginas de ajuda ele tem texto próprio. **Baixar** ao lado de um idioma entrega o arquivo dele com o texto que falta preenchido a partir do inglês, pronto para traduzir.

Para verificar um arquivo antes de enviá-lo, execute `tools\locale-check.ps1 fr.json`. Ele lista chaves ausentes, chaves que esta versão não usa e {marcadores} que não correspondem ao texto em inglês. Adicione `-Help` com uma pasta de páginas de ajuda traduzidas (por exemplo `-Help locales\fr\help`) para verificar essas também: páginas ainda não traduzidas, nomes de arquivo que o LarisVMS não usa, HTML e títulos que não se alinham com a página em inglês.

### Testando uma tradução

Ative **Oferecer o pseudo-idioma no menu de idiomas** em **Configurações → Idioma** e depois escolha-o no menu 🌐. Cada texto aparece acentuado e cerca de um terço mais longo, como `[Ŝàṽé ··]`. O texto que ainda aparece em inglês simples não pode ser traduzido ainda; por favor, informe. O texto que transborda do botão ou da coluna precisa de uma tradução mais curta ou de um ajuste de layout.

### Idiomas da direita para a esquerda

Árabe, hebraico, persa e outros idiomas da direita para a esquerda recebem um layout espelhado: o menu vai para a direita, e o texto e os botões se alinham pela direita. Imagens de câmeras, linhas do tempo e mapas não são espelhados.

### Traduzindo os instaladores

Os instaladores do Web, do Node e do Proxy têm um texto próprio, bem mais curto, em `installer\Common\Strings.en-US.wxl` no código-fonte. Copie-o para um arquivo com o nome do idioma (por exemplo `Strings.fr-FR.wxl`) e siga as notas no topo do arquivo. Os instaladores são então compilados com `.\build-installers.ps1 -Cultures en-US,fr-FR`, o que gera um conjunto de instaladores para cada idioma. Os botões do próprio instalador do Windows (Avançar, Voltar, Cancelar e assim por diante) já estão traduzidos.

## Onde ficam os arquivos de idioma

- `%ProgramData%\LarisVMS\locales` guarda os idiomas adicionados neste servidor. Sobrevive às atualizações.
- `locales` na pasta de instalação do servidor guarda os idiomas que acompanham o LarisVMS. Um arquivo adicionado neste servidor vence, linha a linha, um de mesmo nome aqui.
- O inglês é integrado e não pode ser substituído.

Enviar ou remover um idioma é registrado no log de auditoria e exige permissão para alterar configurações.
