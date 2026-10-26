// Every word on the page, in English and Portuguese, in one module: the components only lay it
// out. The two dictionaries have one shape (Content), so a section cannot exist in one language
// and not the other, and what does not change with the language (the images, their sizes, the
// links, the measurements) is written once below them.
//
// The numbers are the ones measured when the demo images were made: 2026-10-20, Revelei
// 0.1.0, High quality, DirectML on an NVIDIA GeForce RTX 4060 laptop GPU, each the first run of
// its model, so loading it is included.

export const THEME_KEY = "revelei-theme";
export const BASE = "/revelei/";
export const repoUrl = "https://github.com/Japode/revelei";

export const asset = (path: string) => `${BASE}${path}`;

// PB49: the Japode storefront, at the root of the origin this site is served from, so the link
// works on japode.com and on japode.github.io alike.
export const familyHref = "/";
export const familyName = "Japode";

export type Lang = "en" | "pt";
export type Backdrop = "checker" | "white" | "clay" | "night";

/** Each language's route, its <html lang>, and how the other language names it. */
export const languages: Record<Lang, { path: string; htmlLang: string; switchTo: string; switchLabel: string }> = {
  en: { path: "/", htmlLang: "en", switchTo: "PT", switchLabel: "Ver em português" },
  pt: { path: "/pt", htmlLang: "pt-BR", switchTo: "EN", switchLabel: "View in English" },
};

/** The page of a language, with the base. */
export const pageHref = (lang: Lang) => (lang === "en" ? BASE : `${BASE}pt/`);

// --- what does not change with the language ---------------------------------------------

// Every download button goes to the release page, as claude-tray's do: the installer's name
// carries its version, so a direct file link goes stale with the next release, and
// `releases/latest` is the one link that cannot.
export const releasesUrl = `${repoUrl}/releases/latest`;

const heroImages = {
  before: "demo/upscale-before.webp",
  after: "demo/upscale-after.webp",
  width: 1000,
  height: 750,
};

type OperationImages = {
  id: string;
  icon: string;
  model: string;
  size: string;
  time: { en: string; pt: string };
  before: string;
  after: string;
  width: number;
  height: number;
  backdrops?: boolean;
};

const operationImages: OperationImages[] = [
  {
    id: "upscale",
    icon: "⤢",
    model: "Real-ESRGAN x4plus",
    size: "1500 × 1336 px",
    time: { en: "1.9 s", pt: "1,9 s" },
    before: "demo/upscale-nose-before.webp",
    after: "demo/upscale-nose-after.webp",
    width: 1000,
    height: 750,
  },
  {
    id: "denoise",
    icon: "◌",
    model: "SCUNet",
    size: "960 × 720 px",
    time: { en: "3.1 s", pt: "3,1 s" },
    before: "demo/denoise-before.webp",
    after: "demo/denoise-after.webp",
    width: 960,
    height: 720,
  },
  {
    id: "faces",
    icon: "☺",
    model: "YuNet + GFPGAN v1.4",
    size: "824 × 1024 px",
    time: { en: "1.7 s", pt: "1,7 s" },
    before: "demo/faces-before.webp",
    after: "demo/faces-after.webp",
    width: 260,
    height: 260,
  },
  {
    id: "background",
    icon: "◐",
    model: "RMBG-1.4",
    size: "1103 × 755 px",
    time: { en: "1.6 s", pt: "1,6 s" },
    before: "demo/background-before.webp",
    after: "demo/background-after.webp",
    width: 1103,
    height: 755,
    backdrops: true,
  },
];

const creditLinks = [
  "https://commons.wikimedia.org/wiki/File:Red_fox_(8385104980).jpg",
  "https://commons.wikimedia.org/wiki/File:Great_Horned_Owl_(4819455826).jpg",
  "https://commons.wikimedia.org/wiki/File:Full-length_portrait_of_a_young_woman_wearing_a_feathered_hat_LCCN2006689599.jpg",
  "https://commons.wikimedia.org/wiki/File:Gray_Wolf_(6545954933).jpg",
];

// --- the words ------------------------------------------------------------------------------

type OperationWords = {
  name: string;
  verb: string;
  title: string;
  body: string;
  beforeLabel: string;
  afterLabel: string;
  note: string;
};

type Words = {
  meta: { title: string; description: string };
  nav: { links: { href: string; label: string }[]; cta: string; themeLabel: (next: string) => string };
  family: { pre: string; title: string };
  hero: {
    eyebrow: string;
    title: string;
    sub: string;
    primary: string;
    secondary: string;
    hint: string;
    caption: string;
    beforeLabel: string;
    afterLabel: string;
    compareLabel: string;
  };
  studio: {
    eyebrow: string;
    title: string;
    lead: string;
    tabsLabel: string;
    compareLabel: (before: string, after: string) => string;
    backdropLabel: string;
    backdrops: { id: Backdrop; label: string }[];
    modelLabel: string;
    sizeLabel: string;
    timeLabel: string;
    originAlt: string;
    origin: [string, string];
  };
  operations: OperationWords[];
  journey: { eyebrow: string; title: string; steps: { title: string; body: string }[]; scene: { drop: string; ops: string[]; progress: string; file: string; saved: string } };
  privacy: { eyebrow: string; title: string; body: string; facts: { k: string; v: string }[] };
  speed: {
    eyebrow: string;
    title: string;
    lead: string;
    head: [string, string, string];
    rows: { op: string; size: string }[];
    note: string;
  };
  details: { eyebrow: string; title: string; items: { icon: string; title: string; body: string }[] };
  download: {
    eyebrow: string;
    title: string;
    status: string;
    body: string;
    cta: string;
    requirements: string[];
    licensesTitle: string;
    licenses: string;
  };
  credits: { title: string; labels: string[]; by: string[]; note: string };
  footer: { links: { href: string; label: string }[]; disclaimer: string };
};

const en: Words = {
  meta: {
    title: "Revelei: better photos, right on your PC",
    description:
      "Upscale, denoise, restore faces and remove the background of your photos with AI, on your own Windows computer. No cloud, no account, nothing uploaded.",
  },
  nav: {
    links: [
      { href: "#try", label: "Try it" },
      { href: "#how", label: "How it works" },
      { href: "#privacy", label: "Privacy" },
      { href: "#speed", label: "Speed" },
    ],
    cta: "Download",
    themeLabel: (next) => `Switch to the ${next} theme`,
  },
  family: {
    pre: "part of",
    title: "Japode: small software that does one job",
  },
  hero: {
    eyebrow: "For Windows · runs on your computer",
    title: "Your photos, the way you remember them.",
    sub: "Enlarge small photos, wipe out grain, bring faces back into focus and cut out the background. The AI runs on your graphics card, and the photo never leaves your PC.",
    primary: "Try it on this page",
    secondary: "Download for Windows",
    hint: "Drag the line",
    caption: "A 375 × 334 px photo enlarged 4× to 1500 × 1336 px in 1.9 s. On the left, an ordinary enlargement; on the right, Revelei.",
    beforeLabel: "Ordinary enlargement",
    afterLabel: "Revelei 4×",
    compareLabel: "Compare an ordinary enlargement with Revelei",
  },
  studio: {
    eyebrow: "Try it",
    title: "Four things you will want to do with a photo.",
    lead: "Every example below was made by Revelei itself. Pick an operation and drag the line to compare.",
    tabsLabel: "Operations",
    compareLabel: (before, after) => `Compare: ${before} and ${after}`,
    backdropLabel: "Background",
    backdrops: [
      { id: "checker", label: "Transparent" },
      { id: "white", label: "White" },
      { id: "clay", label: "Clay" },
      { id: "night", label: "Night" },
    ],
    modelLabel: "Model",
    sizeLabel: "Result",
    timeLabel: "Time",
    originAlt: "The original photo, 375 × 334 px",
    origin: ["This is the photo the enlargement at the top of the page came from, at its real size: ", "375 × 334 px"],
  },
  operations: [
    {
      name: "Upscale",
      verb: "2× or 4× larger",
      title: "Detail where there was blur.",
      body: "A small photo, from an old phone or cropped from the web, becomes an image large enough to print. The AI does not stretch the pixels: it rebuilds fur and texture.",
      beforeLabel: "Ordinary enlargement",
      afterLabel: "Revelei 4×",
      note: "A crop at full size. The original was reduced to 25% and enlarged back, so there is a reference to compare against.",
    },
    {
      name: "Denoise",
      verb: "Low-light photos",
      title: "Night photos without the grain.",
      body: "Low light and high ISO fill a photo with coloured specks. Revelei takes the noise out and keeps the textures that matter, like bark and feathers.",
      beforeLabel: "Noisy",
      afterLabel: "Denoised",
      note: "The noise in this example was simulated (high ISO: photon, read and colour noise) on a clean photo, to show the effect clearly.",
    },
    {
      name: "Restore faces",
      verb: "Old and small photos",
      title: "Sharp faces in old photos.",
      body: "Revelei finds each face, aligns it, restores it and puts it back with a soft edge. A strength control decides how much of the restoration shows.",
      beforeLabel: "Original",
      afterLabel: "Restored",
      note: "A late 19th-century portrait from the US Library of Congress (public domain). GFPGAN has parts under a non-commercial license.",
    },
    {
      name: "Remove background",
      verb: "Transparent PNG",
      title: "The subject up front. The rest, transparent.",
      body: "One click separates the subject from the scene and saves a transparent PNG, its outline refined against the original photo so no specks are left. Swap the background beside it.",
      beforeLabel: "Original",
      afterLabel: "Background removed",
      note: "RMBG-1.4 is licensed by BRIA for non-commercial use.",
    },
  ],
  journey: {
    eyebrow: "How it works",
    title: "From photo to result in four moves.",
    steps: [
      {
        title: "Drop the photo",
        body: "Drag it from Explorer onto the window, or open it with the button. JPEG, PNG, WebP, TIFF and whatever else Windows opens, upright and in the colours of the camera's profile.",
      },
      {
        title: "Choose what to do",
        body: "Upscale, denoise, restore faces or remove the background. A bar shows the progress and the time left, and you can cancel at any moment.",
      },
      {
        title: "Compare",
        body: "Drag the line across the result, or see them side by side. Zoom in with the mouse wheel down to the pixel. Not happy? Ctrl+Z undoes it without redoing the work.",
      },
      {
        title: "Export",
        body: "PNG, JPEG or WebP, at the quality you choose. Transparency is kept, and the camera details go with it only if you ask.",
      },
    ],
    scene: {
      drop: "Drop a photo here",
      ops: ["Upscale", "Denoise", "Restore faces", "Remove background"],
      progress: "Tile 3 of 6 · about 2 s",
      file: "owl.png",
      saved: "Saved",
    },
  },
  privacy: {
    eyebrow: "Privacy",
    title: "Your photo never leaves your computer.",
    body: "Everything happens on your machine: there is no server, no account and no upload. The AI models live in a folder of yours, and the photo you open is read by them alone.",
    facts: [
      { k: "0", v: "photos sent to the internet" },
      { k: "0", v: "accounts to create" },
      { k: "100%", v: "of the processing on your PC" },
    ],
  },
  speed: {
    eyebrow: "Speed",
    title: "Fast with a graphics card. Works without one.",
    lead: "With a DirectX 12 GPU Revelei runs on DirectML, and on an NVIDIA RTX card it upscales with TensorRT-RTX once Windows has installed it; without a GPU, it runs on the processor. Large photos are processed in tiles, as large as the card's free memory allows.",
    head: ["Operation", "Size", "Time"],
    rows: [
      { op: "Upscale 4×", size: "375 × 334 → 1500 × 1336 px" },
      { op: "Denoise", size: "960 × 720 px" },
      { op: "Restore faces", size: "824 × 1024 px, 1 face" },
      { op: "Remove background", size: "1103 × 755 px" },
    ],
    note: "Measured on a laptop NVIDIA GeForce RTX 4060, at high quality, including loading the model. Without a GPU the same operations take longer and reach the same result.",
  },
  details: {
    eyebrow: "The details",
    title: "Made for people who really edit photos.",
    items: [
      { icon: "↶", title: "History with undo", body: "Every operation becomes a version. Ctrl+Z and Ctrl+Y move between them without running the AI again." },
      { icon: "◫", title: "Comparison on the GPU", body: "A draggable divider, side by side, and zoom down to the pixel, drawn by the graphics card." },
      { icon: "⟳", title: "The right way up", body: "The sideways photo from your phone opens upright, as the camera recorded it." },
      { icon: "◑", title: "Faithful colours", body: "Photos in Display P3 and other profiles are converted to sRGB before anything else." },
      { icon: "▦", title: "Transparency kept", body: "PNG and WebP with transparency stay transparent after they are enlarged." },
      { icon: "⌖", title: "Camera details, if you want", body: "Date, camera and GPS go into the exported file only if you tick the option." },
      { icon: "⏸", title: "Cancellable", body: "A long operation stops at the next tile when you cancel it." },
      { icon: "▤", title: "Memory under control", body: "If the card runs out of memory, the tile is redone smaller, and in the end on the processor." },
    ],
  },
  download: {
    eyebrow: "Download",
    title: "Revelei for Windows.",
    status: "Available",
    body: "A 170 MB installer: upscaling and noise removal work offline from the first time you open it. Face restoration and background removal download their AI models the first time you use them, after telling you the size, the source and the license, and from then on they work offline too. Your photo is never uploaded. A high-quality set, also faster on a PC without a graphics card, is an optional download from the app's About screen.",
    cta: "Download the latest release",
    requirements: [
      "Windows 10 (1903 or later) or Windows 11, 64-bit",
      "A DirectX 12 graphics card to use the GPU; without one, it runs on the processor",
      "Per-user install, no administrator needed",
      "An internet connection the first time you restore faces or remove a background; the models then stay on your PC",
    ],
    licensesTitle: "About the models' licenses",
    licenses:
      "Real-ESRGAN (BSD-3), SCUNet and ViTMatte (Apache-2.0) and YuNet (MIT) are free to use. GFPGAN v1.4 has parts under a non-commercial license, and RMBG-1.4 is licensed by BRIA for non-commercial use only; the app shows each license on its About screen.",
  },
  credits: {
    title: "Example photos",
    labels: ["Red fox", "Great horned owl", "Portrait of a young woman in a feathered hat", "Gray wolf"],
    by: ["USFWS Alaska", "USFWS", "US Library of Congress", "USFWS Midwest Region"],
    note: "All public domain, processed by Revelei for this page.",
  },
  footer: {
    links: [
      { href: "#try", label: "Try it" },
      { href: "#privacy", label: "Privacy" },
      { href: "#download", label: "Download" },
      { href: repoUrl, label: "GitHub" },
      { href: familyHref, label: "More from Japode" },
    ],
    disclaimer: "Revelei is a Japode product. The models' names belong to their authors.",
  },
};

const pt: Words = {
  meta: {
    title: "Revelei: fotos melhores, direto no seu PC",
    description:
      "Amplie, tire o ruído, restaure rostos e remova o fundo das suas fotos com IA, no seu próprio computador com Windows. Sem nuvem, sem conta, sem enviar nada.",
  },
  nav: {
    links: [
      { href: "#try", label: "Experimente" },
      { href: "#how", label: "Como funciona" },
      { href: "#privacy", label: "Privacidade" },
      { href: "#speed", label: "Desempenho" },
    ],
    cta: "Baixar",
    themeLabel: (next) => (next === "dark" ? "Mudar para o tema escuro" : "Mudar para o tema claro"),
  },
  family: {
    pre: "parte da",
    title: "Japode: softwares pequenos que fazem uma coisa bem",
  },
  hero: {
    eyebrow: "Para Windows · roda no seu computador",
    title: "Suas fotos, como você lembrava delas.",
    sub: "Amplie fotos pequenas, apague o granulado, devolva nitidez a rostos e recorte o fundo. A IA roda na sua placa de vídeo, e a foto nunca sai do seu PC.",
    primary: "Experimente aqui na página",
    secondary: "Baixar para Windows",
    hint: "Arraste a linha",
    caption: "Uma foto de 375 × 334 px ampliada 4× para 1500 × 1336 px em 1,9 s. À esquerda, a ampliação comum; à direita, o Revelei.",
    beforeLabel: "Ampliação comum",
    afterLabel: "Revelei 4×",
    compareLabel: "Comparar a ampliação comum com o Revelei",
  },
  studio: {
    eyebrow: "Experimente",
    title: "Quatro coisas que você vai querer fazer com uma foto.",
    lead: "Cada exemplo abaixo foi feito pelo próprio Revelei. Escolha uma operação e arraste a linha para comparar.",
    tabsLabel: "Operações",
    compareLabel: (before, after) => `Comparar: ${before} e ${after}`,
    backdropLabel: "Fundo",
    backdrops: [
      { id: "checker", label: "Transparente" },
      { id: "white", label: "Branco" },
      { id: "clay", label: "Argila" },
      { id: "night", label: "Noite" },
    ],
    modelLabel: "Modelo",
    sizeLabel: "Resultado",
    timeLabel: "Tempo",
    originAlt: "A foto original, 375 × 334 px",
    origin: ["Esta é a foto de onde saiu a ampliação do topo da página, no tamanho real: ", "375 × 334 px"],
  },
  operations: [
    {
      name: "Ampliar",
      verb: "2× ou 4× maior",
      title: "Detalhe onde antes havia borrão.",
      body: "Uma foto pequena, de celular antigo ou recortada da internet, vira uma imagem grande o bastante para imprimir. A IA não estica os pixels: ela reconstrói pelo e textura.",
      beforeLabel: "Ampliação comum",
      afterLabel: "Revelei 4×",
      note: "Recorte em tamanho real. A original foi reduzida a 25% e ampliada de volta, para que haja uma referência com que comparar.",
    },
    {
      name: "Tirar ruído",
      verb: "Fotos com pouca luz",
      title: "Fotos da noite sem o granulado.",
      body: "Pouca luz e ISO alto deixam a foto cheia de pontinhos coloridos. O Revelei tira o ruído e mantém as texturas que importam, como casca de árvore e penas.",
      beforeLabel: "Com ruído",
      afterLabel: "Sem ruído",
      note: "O ruído deste exemplo foi simulado (ISO alto: ruído de fóton, de leitura e de cor) sobre uma foto limpa, para mostrar o efeito com clareza.",
    },
    {
      name: "Restaurar rostos",
      verb: "Fotos antigas e pequenas",
      title: "Rostos nítidos em fotos antigas.",
      body: "O Revelei encontra cada rosto, alinha, restaura e devolve ao lugar com uma borda suave. Um controle de intensidade decide quanto da restauração aparece.",
      beforeLabel: "Original",
      afterLabel: "Restaurado",
      note: "Retrato do fim do século XIX, da Biblioteca do Congresso dos EUA (domínio público). O GFPGAN tem partes sob licença não comercial.",
    },
    {
      name: "Remover fundo",
      verb: "PNG transparente",
      title: "O assunto em primeiro plano. O resto, transparente.",
      body: "Um clique separa o assunto do cenário e salva um PNG transparente, com o contorno refinado contra a foto original para não deixar manchas. Troque o fundo ao lado.",
      beforeLabel: "Original",
      afterLabel: "Sem fundo",
      note: "O RMBG-1.4 é licenciado pela BRIA para uso não comercial.",
    },
  ],
  journey: {
    eyebrow: "Como funciona",
    title: "Da foto ao resultado em quatro gestos.",
    steps: [
      {
        title: "Solte a foto",
        body: "Arraste do Explorer para a janela, ou abra pelo botão. JPEG, PNG, WebP, TIFF e o que mais o Windows abrir, já na orientação certa e com as cores do perfil da câmera.",
      },
      {
        title: "Escolha o que fazer",
        body: "Ampliar, tirar ruído, restaurar rostos ou remover o fundo. Uma barra mostra o progresso e quanto tempo falta, e dá para cancelar a qualquer momento.",
      },
      {
        title: "Compare",
        body: "Arraste a linha sobre o resultado, ou veja lado a lado. Dê zoom com a roda do mouse até o pixel. Não gostou? Ctrl+Z desfaz, sem refazer o trabalho.",
      },
      {
        title: "Exporte",
        body: "PNG, JPEG ou WebP, com a qualidade que você escolher. A transparência é mantida, e os dados da câmera só vão junto se você pedir.",
      },
    ],
    scene: {
      drop: "Solte uma foto aqui",
      ops: ["Ampliar", "Tirar ruído", "Restaurar rostos", "Remover fundo"],
      progress: "Bloco 3 de 6 · cerca de 2 s",
      file: "coruja.png",
      saved: "Salvo",
    },
  },
  privacy: {
    eyebrow: "Privacidade",
    title: "A sua foto nunca sai do seu computador.",
    body: "Tudo acontece na sua máquina: não há servidor, não há conta e não há envio. Os modelos de IA ficam numa pasta sua, e a foto que você abre só é lida por eles.",
    facts: [
      { k: "0", v: "fotos enviadas para a internet" },
      { k: "0", v: "contas para criar" },
      { k: "100%", v: "do processamento no seu PC" },
    ],
  },
  speed: {
    eyebrow: "Desempenho",
    title: "Rápido com placa de vídeo. Funciona sem ela.",
    lead: "Com uma GPU compatível com DirectX 12 o Revelei usa DirectML, e numa placa NVIDIA RTX amplia com o TensorRT-RTX quando o Windows o instalou; sem GPU, roda no processador. Fotos grandes são processadas em blocos, do tamanho que a memória livre da placa permite.",
    head: ["Operação", "Tamanho", "Tempo"],
    rows: [
      { op: "Ampliar 4×", size: "375 × 334 → 1500 × 1336 px" },
      { op: "Tirar ruído", size: "960 × 720 px" },
      { op: "Restaurar rostos", size: "824 × 1024 px, 1 rosto" },
      { op: "Remover fundo", size: "1103 × 755 px" },
    ],
    note: "Medido numa NVIDIA GeForce RTX 4060 de notebook, em qualidade alta, contando o carregamento do modelo. Sem GPU, as mesmas operações levam mais tempo, mas chegam ao mesmo resultado.",
  },
  details: {
    eyebrow: "Os detalhes",
    title: "Pensado para quem edita foto de verdade.",
    items: [
      { icon: "↶", title: "Histórico com desfazer", body: "Cada operação vira uma versão. Ctrl+Z e Ctrl+Y andam entre elas sem rodar a IA de novo." },
      { icon: "◫", title: "Comparação na GPU", body: "Divisor arrastável, lado a lado e zoom até o pixel, desenhados pela placa de vídeo." },
      { icon: "⟳", title: "Orientação certa", body: "A foto deitada do celular abre em pé, como a câmera registrou." },
      { icon: "◑", title: "Cores fiéis", body: "Fotos em Display P3 e outros perfis são convertidas para sRGB antes de tudo." },
      { icon: "▦", title: "Transparência preservada", body: "PNG e WebP com transparência continuam transparentes depois de ampliados." },
      { icon: "⌖", title: "Dados da câmera, se quiser", body: "Data, câmera e GPS só vão para o arquivo exportado se você marcar a opção." },
      { icon: "⏸", title: "Cancelável", body: "Uma operação longa para no bloco seguinte quando você cancela." },
      { icon: "▤", title: "Memória sob controle", body: "Se a placa ficar sem memória, o bloco é refeito menor, e por fim no processador." },
    ],
  },
  download: {
    eyebrow: "Baixar",
    title: "Revelei para Windows.",
    status: "Disponível",
    body: "Um instalador de 170 MB: ampliar e remover ruído funcionam offline desde a primeira vez que você abre. Restaurar rostos e remover o fundo baixam seus modelos de IA na primeira vez que você usa, depois de mostrar o tamanho, a origem e a licença, e a partir daí também funcionam offline. Sua foto nunca é enviada. Um conjunto de alta qualidade, também mais rápido num PC sem placa de vídeo, é um download opcional na tela Sobre do app.",
    cta: "Baixar a versão mais recente",
    requirements: [
      "Windows 10 (1903 ou mais novo) ou Windows 11, 64 bits",
      "Placa de vídeo com DirectX 12 para usar a GPU; sem ela, roda no processador",
      "Instalação por usuário, sem precisar de administrador",
      "Conexão com a internet na primeira vez que você restaura rostos ou remove um fundo; depois os modelos ficam no seu PC",
    ],
    licensesTitle: "Sobre as licenças dos modelos",
    licenses:
      "Real-ESRGAN (BSD-3), SCUNet e ViTMatte (Apache-2.0) e YuNet (MIT) são livres. O GFPGAN v1.4 tem partes sob licença não comercial, e o RMBG-1.4 é licenciado pela BRIA só para uso não comercial; o app mostra cada licença na tela Sobre.",
  },
  credits: {
    title: "Fotos dos exemplos",
    labels: ["Raposa-vermelha", "Coruja-orelhuda", "Retrato de jovem com chapéu de plumas", "Lobo-cinzento"],
    by: ["USFWS Alaska", "USFWS", "Biblioteca do Congresso dos EUA", "USFWS Midwest Region"],
    note: "Todas em domínio público, processadas pelo Revelei para esta página.",
  },
  footer: {
    links: [
      { href: "#try", label: "Experimente" },
      { href: "#privacy", label: "Privacidade" },
      { href: "#download", label: "Baixar" },
      { href: repoUrl, label: "GitHub" },
      { href: familyHref, label: "Mais da Japode" },
    ],
    disclaimer: "Revelei é um produto Japode. Os nomes dos modelos pertencem aos seus autores.",
  },
};

export const words: Record<Lang, Words> = { en, pt };

// --- the two joined, as the components read them ---------------------------------------------

export type Operation = OperationImages & OperationWords & { timeText: string };

/** A language's operations: the shared images with that language's words. */
export function operationsFor(lang: Lang): Operation[] {
  return operationImages.map((img, i) => ({ ...img, ...words[lang].operations[i], timeText: img.time[lang] }));
}

/** The hero's image pair with that language's labels. */
export function heroFor(lang: Lang) {
  return { ...heroImages, ...words[lang].hero };
}

/** The speed table: the operation times are the operations' own. */
export function speedRowsFor(lang: Lang) {
  return words[lang].speed.rows.map((r, i) => ({ ...r, time: operationImages[i].time[lang] }));
}

/** The photo credits, each with its source page. */
export function creditsFor(lang: Lang) {
  const c = words[lang].credits;
  return { ...c, items: creditLinks.map((href, i) => ({ href, label: c.labels[i], by: c.by[i] })) };
}
