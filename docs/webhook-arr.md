# Webhook do Sonarr/Radarr

Quando o Sonarr ou o Radarr importa um arquivo, ele avisa o FilmesApi, que sincroniza o catálogo **na hora** em vez de esperar o
scan periódico (`ScanIntervaloMinutos`, 10 min). O episódio novo aparece na fileira **Novidades** da home em alguns segundos.

## 1. Ligar no servidor

O endpoint `POST /api/webhook/arr` só existe com uma chave configurada; sem ela responde `404` (desligado). Gere uma chave e
ponha no `docker-compose` (ou no `docker-compose.override.yml`), depois recrie o contêiner:

```yaml
    environment:
      - Webhook__Chave=troque-por-uma-chave-longa-e-aleatoria   # ex.: openssl rand -hex 16
```

| Chave | Padrão | O que faz |
|---|---|---|
| `Webhook__Chave` | _(vazia = desligado)_ | Chave compartilhada, enviada pelo *arr em `?chave=`. Comparada em tempo constante; nunca vai pro log. |
| `Webhook__DebounceSegundos` | `12` | Espera depois do último aviso antes de rodar o scan. Vários avisos seguidos (um por episódio) viram **um** scan. |
| `Webhook__EsperaMaximaSegundos` | `60` | Teto da espera, contado do primeiro aviso: uma rajada comprida (season pack) não adia o scan pra sempre. |
| `NovidadesDias` | `14` | Janela da fileira **Novidades** (adicionado nos últimos N dias; `0` = sem corte). |

A chave vai na URL: use só na rede de casa e não suba o nível de log de `Microsoft.AspNetCore` para `Information` (o log de
requisições mostraria a URL).

## 2. Configurar no Sonarr e no Radarr

**Settings → Connect → `+` → Webhook** (o mesmo nos dois):

| Campo | Valor |
|---|---|
| Name | `FilmesApi` |
| On Import / On Download | marcado (no Sonarr v4 há também **On Import Complete**) |
| On Upgrade | marcado |
| On Rename | marcado (opcional) |
| On Series Delete / On Episode File Delete (Sonarr) · On Movie Delete / On Movie File Delete (Radarr) | marcado (opcional: o item some do catálogo na hora) |
| URL | `http://192.168.0.122:8080/api/webhook/arr?chave=XXXX` |
| Method | `POST` |
| Username / Password | vazios |

Não precisa marcar *On Grab*, *On Health Issue* etc. — se vierem, o servidor responde `200` e ignora. Clique em **Test**:
deve dar sucesso (o servidor responde `200 {"ok":true}`). Com a chave errada o *arr mostra erro `401`; com o webhook desligado, `404`.

Se o FilmesApi e o *arr rodam em contêineres na mesma rede Docker, troque o IP pelo nome do serviço (`http://filmes-api:8080/...`).

## 3. O que o servidor faz com cada evento

| `eventType` | Ação |
|---|---|
| `Test` | `200 {"ok":true}` |
| `Download` (importou/upgrade), `ImportComplete`, `Rename`, `EpisodeFileDelete`, `MovieFileDelete`, `SeriesDelete`, `MovieDelete` | pede um scan: responde `200` na hora e roda em segundo plano |
| `Grab`, `Health`, `SeriesAdd`, `MovieAdded`, `ApplicationUpdate`, `ManualInteractionRequired`… | `200 {"ok":true,"ignorado":true}` |
| corpo que não é JSON de webhook do *arr | `400` |

O scan sob demanda usa o mesmo `ScanMediaAsync` do botão **Scan Mídia**: nunca roda dois ao mesmo tempo (o do timer é pulado se
já há um rodando) e funciona mesmo com `ScanIntervaloMinutos=0`. Um arquivo que ainda está sendo copiado fica pro próximo scan
(`ScanEstabilidadeSegundos`).

Testar na mão:

```sh
curl -i -X POST -H 'Content-Type: application/json' -d '{"eventType":"Test"}' \
  'http://192.168.0.122:8080/api/webhook/arr?chave=XXXX'
```

## Novidades

`GET /api/catalogo/novidades?limite=20` devolve `[{filme, novos, rotulo}]`: o que foi adicionado nos últimos `NovidadesDias` dias
e ainda não foi assistido, do mais novo pro mais velho. Os episódios novos da mesma série viram um cartão só
(`"Série · +3 episódios novos"`, `novos: 3`) que aponta para o episódio mais novo ainda não visto. `filme` tem o mesmo formato do
catálogo (`/api/filmes/tela`). Lista vazia = a fileira some do `tv.html` e do `index.html`.
