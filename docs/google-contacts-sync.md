# Google Contacts Sync (anti-463) — obtenção de credenciais

Objetivo: salvar o contato **frio** na conta Google do chip **antes** de disparar, pra ele
sincronizar no aparelho físico primário → WhatsApp → o companion WAHA ganha o **tctoken** → sem 463.

A cadeia SÓ funciona se, por trás do stack, houver um **aparelho físico primário online** com essa
conta Google e o sync de contatos + WhatsApp ligados. (Confirmado pelo operador em 2026-07-22.)

---

## Parte 1 — Google Cloud (uma vez, projeto compartilhado pelos 10)

1. https://console.cloud.google.com → criar/usar um projeto (ex.: `mtrx-contacts`).
2. **APIs & Services → Library** → habilitar **People API**.
3. **APIs & Services → OAuth consent screen**:
   - User type: **External**.
   - App name, e-mail de suporte, e-mail do dev.
   - **Scopes** → adicionar `https://www.googleapis.com/auth/contacts` (é "sensitive").
   - **Test users** → adicionar os e-mails das contas Google dos chips (enquanto em *Testing*, só
     test users conseguem consentir — e NÃO precisa de verificação do Google).
4. **APIs & Services → Credentials → Create credentials → OAuth client ID**:
   - Application type: **Desktop app**.
   - Salvar o **Client ID** e o **Client secret** (são COMPARTILHADOS pelas 10 contas).

---

## Parte 2 — Refresh token de CADA conta de chip (uma vez por conta)

Jeito rápido pra validar (recomendado começar por 1 conta, a do stack A): **OAuth 2.0 Playground**.

1. Abra https://developers.google.com/oauthplayground **logado no browser com a conta Google DAQUELE
   chip** (a sessão do navegador precisa ser a conta do chip — é ela que vai consentir).
2. Engrenagem ⚙ (canto sup. direito) → marque **"Use your own OAuth credentials"** → cole o
   **Client ID** e **Client secret** da Parte 1.
3. No campo da esquerda ("Input your own scopes"), cole:
   `https://www.googleapis.com/auth/contacts` → **Authorize APIs** → escolha a conta → consinta.
4. Clique **"Exchange authorization code for tokens"** → copie o **Refresh token**.
5. Guarde por chip: `e-mail da conta` + `refresh_token`. (O client id/secret é o mesmo pra todos.)

Repita logado em cada conta pros 10. (Alternativa escalável: um mini-helper de consent por loopback
— posso montar depois; pra começar, o Playground resolve.)

---

## Parte 3 — Onde plugar no sistema (por stack)

Por variável de ambiente / config, POR STACK (cada stack usa o refresh token do SEU chip):

```
AddressBookSync__Enabled=true
AddressBookSync__Provider=Google
AddressBookSync__GraceSeconds=180
AddressBookSync__Google__ClientId=<client id compartilhado>
AddressBookSync__Google__ClientSecret=<client secret compartilhado>
AddressBookSync__Google__RefreshToken=<refresh token DO chip deste stack>
```

Com `Enabled=false` (default), nada muda — o pipeline é no-op.

---

## ⚠️ Alerta honesto (muda o rollout)

- **Token de "Testing" morre em 7 dias.** Enquanto o OAuth consent screen estiver em *Testing*, o
  Google expira o refresh token em **7 dias**. Bom o suficiente pra **validar**, ruim pra produção
  contínua.
- **Pra token durável, publicar em "In production".** Com o escopo `contacts` (sensitive) em contas
  Gmail pessoais, publicar pode exigir **verificação do Google** (privacy policy, revisão, dias de
  espera). Se as contas fossem de um **Google Workspace** da org, dava pra marcar "Internal" e pular
  a verificação — não é o caso de Gmail pessoal.

### Sequência recomendada
1. Pegue **1 refresh token** (conta do stack A) pelo Playground — modo Testing, os 7 dias bastam.
2. Buildo o provider Google + o pipeline; ligo só no A.
3. **Testo de verdade:** salva 1 frio → espera o grace → dispara → chegou (ack≥2) **sem 463**?
4. Se SIM: aí vale o trabalho de publicar/verificar o consent screen e pegar os 10 tokens duráveis.
   Se NÃO: economizamos o rollout — o caminho é emulador-primary ou sair do grátis.
