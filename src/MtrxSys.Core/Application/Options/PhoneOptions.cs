namespace MtrxSys.Core.Application.Options;

/// <summary>Config do "aparelho virtual" da aba "Celular": um Android em container (docker-android)
/// que a API provisiona/liga/instala/registra TUDO pela aba — sem prompt, sem script. O Android vira
/// o dispositivo PRINCIPAL do número (registro por SMS) e o WAHA fica como companion pro disparo.
/// Exige um host com /dev/kvm (servidor Linux). Ver docs/phone.md.</summary>
public sealed class PhoneOptions
{
    public const string SectionName = "Phone";

    /// <summary>Engine do aparelho virtual: "docker-android" (budtmo, exige /dev/kvm — default) ou
    /// "redroid" (Android no kernel do host via binder/ashmem, SEM KVM, ~0.5-1 GB/instância; a tela
    /// embute via ws-scrcpy por adb). Seleciona a implementação de IPhoneOrchestrator no DI.</summary>
    public string Engine { get; set; } = "docker-android";

    /// <summary>Serial do aparelho FÍSICO no `adb devices` (ex.: RQ8WB048RFW). Só usado quando
    /// <see cref="Engine"/> = "physical".</summary>
    /// <remarks>
    /// Passar o serial explicitamente, e não confiar no "aparelho único", é o que evita o modo de falha
    /// silencioso: com dois celulares plugados na mesma máquina o adb responde "more than one device"
    /// — ou, pior, acerta o aparelho do chip errado. Ver docs/engine-physical.md.
    /// </remarks>
    public string AdbSerial { get; set; } = "";

    /// <summary>Executável do adb. Default "adb" (resolve pelo PATH); aceita caminho absoluto.</summary>
    /// <remarks>
    /// 🔴 Existe porque "está no PATH" é premissa que falha nos dois ambientes que importam: no Windows
    /// o platform-tools do Android SDK não entra no PATH por padrão, e em container o PATH não tem adb
    /// nenhum. Quando falha, o <c>Process.Start</c> estoura, o runner devolve código -1 e o engine
    /// reporta "unavailable" — indistinguível de cabo solto, que é o diagnóstico errado.
    /// </remarks>
    public string AdbPath { get; set; } = "adb";

    /// <summary>Nome do container do Android deste ambiente (ex.: mtrx-android / mtrx2-android).</summary>
    public string ContainerName { get; set; } = "mtrx-android";

    /// <summary>Imagem do Android em container usada ao provisionar pela aba.</summary>
    public string Image { get; set; } = "budtmo/docker-android:emulator_14.0";

    /// <summary>Perfil de aparelho do emulador (EMULATOR_DEVICE). Precisa ser um nome suportado pela
    /// imagem docker-android (ex.: "Samsung Galaxy S10", "Pixel 8", "Nexus 5") — "Pixel 6" NÃO é
    /// suportado pela emulator_14.0.</summary>
    public string Device { get; set; } = "Samsung Galaxy S10";

    /// <summary>Resolução do display do docker-android (SCREEN_WIDTH/HEIGHT/DEPTH ao provisionar). 404×850
    /// casa EXATO com o aspect-ratio do iframe da aba Celular (404/850): a tela preenche sem sobrar faixa
    /// lateral — onde a barra de controle do emulador apareceria. O default do budtmo é 500 (deixa ~98px
    /// de faixa com a barra visível). O A foi criado com 404; sem setar isto, os stacks provisionados pela
    /// aba caíam no 500 e a tela não ficava limpa como a do A. Ver o clean_screen do emulator-watchdog.sh.</summary>
    public int ScreenWidth { get; set; } = 404;

    /// <inheritdoc cref="ScreenWidth"/>
    public int ScreenHeight { get; set; } = 850;

    /// <inheritdoc cref="ScreenWidth"/>
    public int ScreenDepth { get; set; } = 24;

    /// <summary>Porta do host pro noVNC (tela do Android) ao provisionar. Embutida na aba.</summary>
    public int NoVncPort { get; set; } = 6080;

    /// <summary>Args extras passados ao emulador (EMULATOR_ADDITIONAL_ARGS do docker-android, appendados
    /// DEPOIS dos args default). Default "-gpu swangle_indirect": troca o renderizador default
    /// (swiftshader_indirect), que jogava GL_INVALID_OPERATION (0x502) em rajada e CRASHAVA o qemu ao
    /// abrir o WhatsApp (provado no A 2026-07-24). Como o emulator.py appenda estes args por último e o
    /// último "-gpu" vence, isto sobrescreve o default sem tocar a imagem. Vale pros 10 (o A já usa via
    /// emulator-a.yml; aqui garante o mesmo pros B-J provisionados por docker-run). Vazio = default da
    /// imagem (NÃO recomendado — reintroduz o crash). Ver docs/rebuild-emulator-a.md.</summary>
    public string EmulatorAdditionalArgs { get; set; } = "-gpu swangle_indirect";

    /// <summary>Volume Docker que guarda o estado do Android (app + sessão do WhatsApp).</summary>
    public string VolumeName { get; set; } = "android-data";

    /// <summary>URL do noVNC a embutir na aba quando o Android está no ar (ex.: http://localhost:6080).
    /// Vazio = a aba só mostra status/controle.</summary>
    public string ViewUrl { get; set; } = "";

    /// <summary>URL do APK do WhatsApp pro botão "Instalar WhatsApp" (sideload). Você fornece (não há
    /// URL oficial estável). Vazio = a aba explica como instalar manualmente.</summary>
    public string WhatsAppApkUrl { get; set; } = "";

    /// <summary>Pacote do app a controlar (clear/grant/launch/ler número): "com.whatsapp" (WhatsApp
    /// comum — default) ou "com.whatsapp.w4b" (WhatsApp Business — perfil de empresa, mais legítimo pro
    /// destinatário, reduz denúncia "quem é esse?"). O APK instalado (local ou WhatsAppApkUrl) PRECISA
    /// bater com este pacote — senão o clear/grant miram um app que não está instalado.</summary>
    public string WhatsAppPackage { get; set; } = "com.whatsapp";

    // ── Engine redroid (usados só quando Engine = "redroid") ──────────────────────────────────

    /// <summary>Imagem do redroid ao provisionar. Escolha a versão do Android + arch (ex.:
    /// redroid/redroid:14.0.0-latest, redroid/redroid:11.0.0-latest).</summary>
    public string RedroidImage { get; set; } = "redroid/redroid:14.0.0-latest";

    /// <summary>redroid precisa de privilégios pro binder/ashmem do kernel do host (docker run
    /// --privileged). Default true (funciona out-of-the-box). Ponha false só num host endurecido que
    /// conceda as caps específicas por fora.</summary>
    public bool RedroidPrivileged { get; set; } = true;

    /// <summary>Host por onde a API alcança o adb do redroid. Default host.docker.internal (a API roda
    /// em container bridged; o redroid publica o adb em 0.0.0.0:AdbPort no host e a API chega por
    /// host-gateway). Exige extra_hosts host-gateway no serviço da api. Serial = "{AdbHost}:{AdbPort}".</summary>
    public string AdbHost { get; set; } = "host.docker.internal";

    /// <summary>Porta HOST do adb do redroid (docker run -p AdbPort:5555, bind 0.0.0.0 — travado no
    /// firewall). ÚNICA por ambiente (1 host, 10 stacks): A=5555, B=5556, … J=5564.</summary>
    public int AdbPort { get; set; } = 5555;

    /// <summary>Resolução/densidade do redroid (androidboot.redroid_width/height/dpi). 720x1280@320
    /// é leve e suficiente pro WhatsApp.</summary>
    public int Width { get; set; } = 720;

    /// <inheritdoc cref="Width"/>
    public int Height { get; set; } = 1280;

    /// <inheritdoc cref="Width"/>
    public int Dpi { get; set; } = 320;

    /// <summary>Modo de GPU do redroid (androidboot.redroid_gpu_mode). "guest" = render por software
    /// (sem GPU do host) — o mais portável pros servidores. "host" exige GPU + drivers no host.</summary>
    public string GpuMode { get; set; } = "guest";

    // ── Tetos de recurso do container on-demand (docker run) ──────────────────────────────────

    /// <summary>Teto de RAM do emulador (docker run --memory). O docker-android (S10, 1440x3040) usa
    /// ~6 GiB em regime, então 8g dá folga sem OOM. Como só 1-2 emuladores ligam por vez (lifecycle de
    /// keep-alive), o teto por-aparelho pode ser generoso. Vazio = sem teto.</summary>
    public string MemoryLimit { get; set; } = "8g";

    /// <summary>Teto de CPU do emulador (docker run --cpus). 4 acelera o boot (o maior gasto de CPU);
    /// em idle o emulador fica perto de 0. Vazio = sem teto.</summary>
    public string Cpus { get; set; } = "4";

    /// <summary>Política de restart (docker run --restart). Default "no": o primário fica DESLIGADO no
    /// regime normal (acordamos só no keep-alive), então não deve voltar sozinho após reboot do host.
    /// "unless-stopped" reproduz o comportamento antigo (24/7).</summary>
    public string RestartPolicy { get; set; } = "no";

    // ── Keep-alive do primário (o WhatsApp desloga o companion se o principal sumir ~14 dias) ──

    /// <summary>Liga o loop que acorda o primário periodicamente e o desliga após parear.</summary>
    public bool KeepAliveEnabled { get; set; } = true;

    /// <summary>Intervalo entre keep-alives, em horas. Default 240h (10 dias) — margem de 4 dias sob o
    /// limite de ~14 do WhatsApp.</summary>
    public int KeepAliveIntervalHours { get; set; } = 240;

    /// <summary>Período do tick do serviço (segundos). Curto pra o stop-after-pair reagir rápido.</summary>
    public int KeepAliveTickSeconds { get; set; } = 60;

    /// <summary>Após o WAHA vincular (WORKING), desliga o emulador pra poupar recurso.</summary>
    public bool StopAfterPairEnabled { get; set; } = true;

    /// <summary>Carência (min) entre ver o WAHA WORKING e desligar o primário — deixa o sync inicial de
    /// histórico terminar.</summary>
    public int StopAfterPairGraceMinutes { get; set; } = 5;

    /// <summary>Minutos que o primário fica online durante o keep-alive antes de desligar de novo.</summary>
    public int KeepAliveHoldMinutes { get; set; } = 3;

    /// <summary>Teto (s) pra esperar o Android bootar no keep-alive.</summary>
    public int KeepAliveBootTimeoutSeconds { get; set; } = 240;

    /// <summary>Teto (s) pra esperar o WAHA voltar a WORKING após o wake.</summary>
    public int KeepAliveReconnectTimeoutSeconds { get; set; } = 180;

    /// <summary>Slot de escalonamento 0-9 (janela do dia em que este stack acorda). -1 = derivar do
    /// ContainerName por hash estável, pra os 10 não acordarem juntos.</summary>
    public int KeepAliveStaggerSlot { get; set; } = -1;

    // ── Envio pela UI do WhatsApp (Caminho A anti-463 — envio pelo PRIMÁRIO, não pelo WAHA) ──────
    // Automação de UI (docker-android): abre o chat via intent click-to-chat (mensagem já preenchida),
    // acha o botão "enviar" por resource-id via uiautomator (ROBUSTO — sem coords fixas), toca, e
    // confirma o envio pelo campo de texto esvaziar. Estes são TIMEOUTS de poll, não sleeps fixos.

    /// <summary>Timeout (ms) do poll pelo botão "enviar" aparecer após abrir o chat. Chat lento
    /// (cold start / proxy) precisa de folga; poll re-tenta até aqui em vez de abortar num sleep fixo.</summary>
    public int WhatsAppOpenWaitMs { get; set; } = 6000;

    /// <summary>Timeout (ms) do poll de CONFIRMAÇÃO do envio (o campo de texto esvaziar) após tocar enviar.</summary>
    public int WhatsAppSendWaitMs { get; set; } = 3000;

    /// <summary>Timeout TOTAL (s) de um envio pela UI. Cada chamada adb já tem seu teto (60s), mas um
    /// envio faz várias (intent + polls + tap); sem um teto total, um emulador travado seguraria o lock
    /// de UI por minutos e travaria a fila. Aborta o envio (retorna falha) e libera o lock passado isto.
    /// <para>⚠️ Com <see cref="HumanTyping"/> ligado, a DIGITAÇÃO entra dentro deste teto. Uma mensagem
    /// de 800 caracteres a 260 cpm leva ~3 min, então 90s aborta todo envio no meio. Ver
    /// <see cref="TypingCharsPerMinute"/>.</para></summary>
    public int WhatsAppSendTimeoutSeconds { get; set; } = 90;

    // ── Digitação humana ─────────────────────────────────────────────────────────────────────────
    // O deep link click-to-chat entrega a mensagem PRONTA no campo. Isso não deixa rastro de digitação:
    // zero evento de tecla, zero indicador "digitando…" (que o app dispara a partir do TextWatcher do
    // campo) e 800 caracteres aparecendo em ~3s. É a diferença mais visível entre este envio e uma
    // pessoa, e ela não é afetada por nenhum ajuste no intervalo ENTRE mensagens.

    /// <summary>Digitar a mensagem no aparelho em vez de entregá-la pronta pelo deep link.</summary>
    /// <remarks>
    /// Quando ligado e não houver como digitar o texto, o envio FALHA em vez de cair no caminho antigo.
    /// É deliberado: o silêncio de um fallback devolveria justamente o comportamento que se quis remover,
    /// e ninguém saberia. Falha aparece no log e o job volta pra fila.
    /// </remarks>
    public bool HumanTyping { get; set; } = true;

    /// <summary>Pacote do IME que aceita texto Unicode por broadcast (ADB Keyboard).</summary>
    /// <remarks>
    /// Necessário porque o `input text` do Android NÃO digita acento nem emoji: devolve
    /// NullPointerException (medido no aparelho de produção, Android 14). Sem o IME só dá pra digitar
    /// texto ASCII simples, e um template em português sem acento é mais robótico, não menos.
    /// </remarks>
    public string TypingImePackage { get; set; } = "com.android.adbkeyboard";

    /// <summary>Componente do IME (pacote/serviço) usado no `ime enable` / `ime set`.</summary>
    public string TypingImeComponent { get; set; } = "com.android.adbkeyboard/.AdbIME";

    /// <summary>URL http(s) do APK do teclado. Vazio = não instala (usa o que já estiver no aparelho).</summary>
    public string TypingImeApkUrl { get; set; } = "";

    /// <summary>Ritmo alvo da digitação em caracteres por minuto (com variação a cada trecho).</summary>
    /// <remarks>
    /// 260 cpm fica na faixa alta do que uma pessoa digita no celular, sem virar absurdo. Subir demais
    /// devolve o problema (texto instantâneo); baixar demais estrangula a vazão: no platô de 200
    /// mensagens/dia, cada minuto a mais por mensagem custa 3h20 de janela de envio.
    /// </remarks>
    public int TypingCharsPerMinute { get; set; } = 260;
}
