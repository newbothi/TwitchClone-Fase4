using System.Globalization;
using System.Text;

namespace TwitchClonePorSeparadoConMongoMasSusurros.Web.Simulation
{
    // Cómo se comporta cada bot. Los porcentajes imitan un chat real de Twitch:
    // la mayoría mira sin escribir, unos pocos escriben casi todo.
    public enum BotPersona
    {
        Lurker,        // 55 %: casi nunca escribe
        Casual,        // 25 %: comenta de vez en cuando
        Hype,          //  8 %: "VAMOOO", "POG", mayúsculas
        Questioner,    //  6 %: pregunta cosas al streamer
        EmoteSpammer,  //  5 %: solo emotes
        Troll          //  1 %: provoca (y a veces cae en los filtros)
    }

    public enum ContentTopic { General, Gaming, Music, Talk, Art, Food, Sports, Tech, Travel, Asmr }

    // Datos del canal que se usan para rellenar las plantillas.
    public sealed class ContentContext
    {
        public string Streamer { get; init; } = "streamer";
        public string Category { get; init; } = "el stream";
        public IReadOnlyList<string> ChannelEmotes { get; init; } = Array.Empty<string>();
        public Func<string?> RandomChatter { get; init; } = () => null;
    }

    // Banco de mensajes del simulador. Plantillas con {streamer}, {city}, {emote},
    // {user}, {n} y {category}; después se les agrega "ruido" (minúsculas, sin tildes,
    // letras repetidas, un emote al final) para que no se repitan textualmente.
    public static class ChatContentLibrary
    {
        public static readonly string[] GlobalEmotes =
        {
            "KEKW", "LUL", "PogChamp", "Pog", "monkaS", "Kappa", "PepeHands", "Sadge", "OMEGALUL", "EZ",
            "catJAM", "FeelsGoodMan", "5Head", "Jebaited", "BibleThump", "<3", "xD", "😂", "🔥", "💜",
            "👀", "🤣", "😭", "🙌", "💀", "😎", "🫡", "🤡"
        };

        public static readonly string[] Cities =
        {
            "Santiago", "Valpo", "Viña", "Concepción", "Temuco", "Antofa", "La Serena", "Puerto Montt", "Arica",
            "Iquique", "Punta Arenas", "Rancagua", "Talca", "Chillán", "Osorno", "Calama", "Lima", "Buenos Aires",
            "CDMX", "Bogotá", "Montevideo", "Quito", "Madrid", "Mendoza", "Guadalajara"
        };

        // Tokens que se vuelven "ola" (todos escriben lo mismo unos segundos).
        public static readonly string[] WaveTokens =
        {
            "KEKW", "POG", "GG", "F", "LETS GOOO", "NOOOO", "W", "L", "OMEGALUL", "PogChamp", "jajajaja", "CINE 🎬",
            "INSANO", "monkaS", "???", "CLIP IT", "EZ"
        };

        public static readonly string[] Copypastas =
        {
            "SI ESTE MENSAJE LLEGA A 10 PERSONAS {streamer} SE TOMA UN VASO DE AGUA 💧",
            "🚨 ALERTA DE JUGADA ÉPICA 🚨 ALERTA DE JUGADA ÉPICA 🚨",
            "{streamer} NUNCA PIERDE, SOLO APRENDE 🧠",
            "ESTO ES CINE 🎬🍿 ESTO ES CINE 🎬🍿",
            "chat escriban 1 si vienen del clip",
            "el que lea esto es el más bkn del chat 😎",
            "⬆️ ese mensaje no lo lee nadie ⬆️",
            "MODS ⚔️ PROTEJAN AL STREAMER ⚔️",
            "TODOS A SPAMEAR {emote} {emote} {emote}",
            "{streamer} > todos los streamers de {category}"
        };

        private static readonly string[] General =
        {
            "jajaja", "jajaja buena", "buenísimo el stream", "bkn el stream hoy", "qué weá jaja", "xd", "XDDD",
            "primera vez por acá, se ve bueno", "saludos desde {city}", "buenas desde {city} 👋", "{streamer} eres el mejor",
            "la calidad se ve muy bien", "se trabó un poco o soy yo?", "ya volvió, era mi internet", "{emote}",
            "ese momento {emote}", "jajajaja no puede ser", "wena wena", "uff", "nooo", "sí po", "tremendo",
            "buena buena", "jajaja el chat", "alguien más viendo desde el celu?", "llegué tarde, qué me perdí?",
            "+1", "verdad", "obvio", "eso", "jaja sí", "nah", "puede ser", "{streamer} saluda!!", "qué buena música de fondo",
            "{user} jajaja", "@{user} jaja verdad", "@{user} x2", "mañana trabajo y sigo aquí", "una más y me voy a dormir",
            "chat está tranquilo hoy", "chat despierten", "el mejor stream de {category}", "me encanta este canal",
            "follow dado 💜", "ya me suscribiría si pudiera jaja", "me quedo hasta el final", "esto está buenísimo"
        };

        private static readonly Dictionary<ContentTopic, string[]> ByTopic = new()
        {
            [ContentTopic.Gaming] = new[]
            {
                "qué build estás usando?", "esa jugada estuvo limpia", "nooo casi", "outplay total", "dale que se puede",
                "ese boss es imposible", "andá por la izquierda", "te faltó vida ahí", "qué sensibilidad usas?", "ese enemigo era fácil jaja",
                "GG", "gg wp", "manco jajaja (con cariño)", "vas a intentarlo de nuevo?", "cuántas muertes llevamos?",
                "jugaste el DLC?", "ese arma está rota", "nerf ya", "skill issue", "aim de dios", "clutch",
                "qué lag de servidor", "tiene que ir a la base", "compra armadura", "usa la ulti", "hay que farmear más",
                "la última partida estuvo épica", "speedrun cuando?", "eso fue un bug?", "te robaron la kill"
            },
            [ContentTopic.Music] = new[]
            {
                "qué temazo 🎶", "cómo se llama esta canción?", "este beat está muy bueno", "catJAM catJAM", "subele el volumen",
                "la mezcla está perfecta", "toca algo de los 80", "esto para estudiar es lo mejor", "chill total", "esa transición 🔥",
                "qué DAW usas?", "saca un disco ya", "pon el link de la playlist", "me relaja demasiado", "el bajo suena increíble",
                "toca la de la semana pasada!", "mood", "quedé pegado con este tema", "{emote} 🎶", "bpm?"
            },
            [ContentTopic.Talk] = new[]
            {
                "concuerdo totalmente", "eso mismo pienso yo", "no estoy de acuerdo jaja", "cuenta la historia completa",
                "qué opinas de lo que pasó ayer?", "jajaja me pasó lo mismo", "buen tema para hoy", "story time!!",
                "eso es muy real", "nadie habla de esto", "{streamer} filósofo", "y después qué pasó?", "noo no puede ser",
                "tremenda anécdota", "hagan un podcast", "tema polémico jaja", "la gente de {city} lo sabe", "so true"
            },
            [ContentTopic.Art] = new[]
            {
                "qué lindos colores", "ese sombreado está increíble", "qué pincel usas?", "me encanta el estilo", "dibuja un gato!!",
                "cuánto te demoras en uno así?", "esto va para portafolio?", "los ojos quedaron perfectos", "vende prints!",
                "qué tablet usas?", "el fondo está muy bueno", "tremendo talento", "yo no sé dibujar ni un palito jaja",
                "timelapse después?", "esa paleta 😍", "le falta más contraste?", "se ve muy pro"
            },
            [ContentTopic.Food] = new[]
            {
                "se ve muy rico 🤤", "receta en la descripción?", "cuánto rato al horno?", "le falta sal jaja", "eso con pebre queda mejor",
                "me dio hambre", "qué olor debe tener eso", "haz sopaipillas!!", "yo le pondría ajo", "ese cuchillo corta todo",
                "mi abuela lo hace igual", "cuántas porciones salen?", "cuidado que se quema!", "se puede hacer sin horno?",
                "invita jaja", "chef profesional", "qué aceite usas?", "eso es mucha mantequilla jaja"
            },
            [ContentTopic.Sports] = new[]
            {
                "vamos que se puede 💪", "cuántas series van?", "qué rutina tan dura", "me duelen las piernas de mirar", "hidratación!",
                "postura perfecta", "cuánto levantas?", "yo hago eso y me muero", "ese ejercicio sirve para la espalda?", "descanso 1 minuto",
                "la constancia es todo", "qué proteína tomas?", "a entrenar chat!", "100 sentadillas por cada follow jaja", "beast mode"
            },
            [ContentTopic.Tech] = new[]
            {
                "qué lenguaje es ese?", "falta un punto y coma jaja", "eso compila?", "usa LINQ ahí", "qué IDE es?", "modo oscuro obvio",
                "ese bug era del cache", "haz un commit antes!", "funciona en mi máquina 😂", "tests primero", "sube el repo a GitHub",
                "qué teclado usas?", "eso con Docker sale más fácil", "Stack Overflow al rescate", "refactor time", "deploy un viernes nooo"
            },
            [ContentTopic.Travel] = new[]
            {
                "qué lugar más lindo", "dónde estás ahora?", "se ve frío jaja", "muestra la vista!", "anda al mirador", "cuidado con la cámara",
                "yo fui ahí el año pasado", "qué bacán viajar así", "come algo típico!", "cuántos km caminaste?", "el atardecer 😍",
                "se escucha el viento", "la señal aguanta harto", "recomienda hostales!"
            },
            [ContentTopic.Asmr] = new[]
            {
                "me relaja mucho", "ese sonido 😴", "tapping!!", "me voy a dormir con esto", "más susurros", "micro binaural?",
                "el mejor ASMR", "chat en silencio por favor jaja", "tingles", "😴😴", "qué calma"
            }
        };

        private static readonly string[] Hype =
        {
            "VAMOOOOS", "LETS GOOOO", "POGGERS", "INSANO", "QUÉ JUGADA", "NOOOOO", "SÍIIII", "TREMENDO", "CLIP IT", "ESO ES",
            "GOD", "WTF", "KEKW KEKW", "Pog Pog Pog", "{streamer} GOD", "LEYENDA", "NO PUEDE SER", "🔥🔥🔥", "ESTO ES HISTORIA",
            "CHAT VIERON ESO?", "ASDKJASKD", "{emote} {emote}", "VAMOS {streamer}!!!", "GG EZ", "AAAAAA"
        };

        private static readonly string[] Questions =
        {
            "a qué hora streameas mañana?", "qué micrófono usas?", "qué cámara tienes?", "hay discord?", "cuántas horas llevas?",
            "de dónde eres {streamer}?", "cuánto llevas haciendo streams?", "qué specs tiene tu pc?", "vas a hacer evento especial?",
            "cuál es tu {category} favorito?", "tienes Instagram?", "por qué empezaste a streamear?", "juegas con subs algún día?",
            "qué opinas de {category}?", "hasta qué hora es hoy?", "qué tomas mientras streameas?", "tienes mascota?",
            "cómo se consiguen puntos del canal?", "{streamer} me saludas?", "se puede pedir canción/tema?"
        };

        private static readonly string[] Trolls =
        {
            "aburrido", "este stream está muerto", "cambia de {category} mejor", "cringe", "0 skill", "zzzz",
            "el otro streamer es mejor", "ban me", "quién ve esto jaja", "sigan mi canal en ww.mi-canal-falso.com",
            "regalo puntos gratis en bit.ly/puntos-gratis", "{streamer} manco", "boring", "vine por el raid y me voy",
            "nadie te ve", "chat bots", "L streamer"
        };

        private static readonly string[] Greetings =
        {
            "hola", "holaa", "holaaa chat", "buenas", "wena", "hola {streamer}!", "buenas noches desde {city}", "llegué 👋",
            "presente", "hola hola", "qué tal chat", "hola {streamer} 💜", "buenas tardes", "wenas", "hola a todos", "o/"
        };

        private static readonly string[] GreetBack =
        {
            "hola @{user}", "holaa @{user} 👋", "buenas @{user}", "wena @{user}", "hola @{user} bienvenido", "@{user} holaa <3"
        };

        private static readonly string[] Answers =
        {
            "sí", "nop", "obvio", "nah", "puede ser", "sipo", "obviamente", "jamás", "creo que sí", "ni idea", "depende jaja",
            "sí sí", "no creo", "100%", "eso", "claro"
        };

        private static readonly string[] ReplyToMention =
        {
            "@{user} jaja qué", "@{user} holaa!", "@{user} ?", "@{user} sí po", "@{user} jajaja verdad", "@{user} nooo",
            "@{user} te leo", "@{user} jaja tranqui", "@{user} 👀", "@{user} x2"
        };

        private static readonly string[] ReplyToQuestion =
        {
            "@{user} creo que sí", "@{user} nose", "@{user} pregúntale al streamer", "@{user} lo dijo al principio",
            "@{user} está en los paneles abajo", "@{user} nop", "@{user} sí, a las 9", "@{user} buena pregunta",
            "@{user} creo que no", "@{user} dijo que después"
        };

        private static readonly string[] StreamerReactions =
        {
            "jajaja", "jajaja verdad", "+1", "eso", "LUL", "KEKW", "sí", "jaja dale", "buena {streamer}", "😂😂", "nooo",
            "jajaja {streamer}", "real", "F", "{emote}"
        };

        private static readonly string[] RaidLines =
        {
            "🚀 RAID DE {source} 🚀", "{source} RAID PogChamp", "venimos de lo de {source}!!", "holaa venimos del raid",
            "RAID RAID RAID", "{source} manda saludos 💜", "{emote} RAID {emote}", "llegamos 🚀", "el raid llegó jaja hola"
        };

        private static readonly string[] SanctionReactions = { "F", "KEKW", "se lo buscó", "F en el chat", "ban merecido", "jajaja chao", "🫡" };

        private static readonly string[] ClearReactions = { "?", "qué pasó jaja", "F chat", "se borró todo", "???", "el chat se fue 💀" };

        private static readonly string[] PollReactions =
        {
            "voté por la {option}", "la {option} obvio", "{option} sin duda", "ya voté", "votenn", "la {option} gana fijo",
            "mmm difícil", "votaré la {option}"
        };

        private static readonly string[] PollEndReactions = { "GG", "sabía", "robado jaja", "lo sabía", "era obvio", "nooo mi opción", "democracia 🫡" };

        private static readonly string[] NewModReactions = { "felicidades @{user} 🎉", "nuevo mod PogChamp", "@{user} con poder ⚔️", "GG @{user}", "abuso de poder en 3, 2, 1 jaja" };

        private static readonly string[] RaidWelcome =
        {
            "bienvenidos raiders 💜", "hola gente de {source}!", "RAID PogChamp", "bienvenidos!!", "hola raid 👋",
            "{source} siempre trae buena gente", "llegó el raid 🚀", "wena raiders"
        };

        private static readonly string[] RaidOutgoing =
        {
            "🚀🚀🚀", "vamos!!", "RAID 🚀", "a lo de {target}!", "nos vamos de raid", "gracias por el stream {streamer}!", "vamos todos 🚀"
        };

        private static readonly string[] StreamStart =
        {
            "LLEGÓ", "empezó!!", "holaa {streamer}", "VAMOOO", "por fin", "presente 🙋", "hola chat, empezó", "buenas {streamer}!!", "primero jaja"
        };

        private static readonly string[] StreamEnd =
        {
            "gg buen stream", "chao {streamer}!", "nos vemos mañana", "gracias por el stream 💜", "buen stream", "byee", "F se acabó", "descansa {streamer}"
        };

        public static readonly string[] DonationMessages =
        {
            "para el agua 💧", "gran stream!", "vamos {streamer}!!", "por la jugada de antes", "", "", "sigue así 💜",
            "un aporte pa la causa", "saludos desde {city}", "lo prometido es deuda jaja", "para que compres un micro nuevo"
        };

        public static ContentTopic TopicFor(string? category)
        {
            var c = Normalize(category ?? string.Empty);
            if (c.Length == 0) return ContentTopic.General;
            if (c.Contains("asmr")) return ContentTopic.Asmr;
            if (c.Contains("musica") || c.Contains("dj")) return ContentTopic.Music;
            if (c.Contains("arte") || c.Contains("dibujo") || c.Contains("ilustr")) return ContentTopic.Art;
            if (c.Contains("comida") || c.Contains("cocina") || c.Contains("bebida")) return ContentTopic.Food;
            if (c.Contains("deporte") || c.Contains("fitness") || c.Contains("entren")) return ContentTopic.Sports;
            if (c.Contains("ciencia") || c.Contains("tecnolog") || c.Contains("program")) return ContentTopic.Tech;
            if (c.Contains("viaje") || c.Contains("aire libre")) return ContentTopic.Travel;
            if (c.Contains("charlando") || c.Contains("talk") || c.Contains("podcast")) return ContentTopic.Talk;
            if (c.Contains("juego") || c.Contains("game") || c.Contains("league") || c.Contains("fighter")
                || c.Contains("elden") || c.Contains("minecraft") || c.Contains("fortnite") || c.Contains("valorant"))
                return ContentTopic.Gaming;
            return ContentTopic.General;
        }

        // ── Generadores por tipo de mensaje ────────────────────────────────

        public static string ForPersona(BotPersona persona, ContentTopic topic, ContentContext ctx, Random rng) => persona switch
        {
            BotPersona.Hype => Noise(Fill(Pick(Hype, rng), ctx, rng), rng, allowLower: false),
            BotPersona.Questioner => Noise(Fill(Pick(Questions, rng), ctx, rng), rng),
            BotPersona.EmoteSpammer => EmoteSpam(ctx, rng),
            BotPersona.Troll => Fill(Pick(Trolls, rng), ctx, rng),
            BotPersona.Lurker => Noise(Fill(rng.NextDouble() < 0.5 ? Pick(Greetings, rng) : Pick(General, rng), ctx, rng), rng),
            _ => Casual(topic, ctx, rng)
        };

        public static string Casual(ContentTopic topic, ContentContext ctx, Random rng)
        {
            var roll = rng.NextDouble();
            string template;
            if (roll < 0.5 && ByTopic.TryGetValue(topic, out var topical)) template = Pick(topical, rng);
            else if (roll < 0.9) template = Pick(General, rng);
            else template = "{emote}";
            return Noise(Fill(template, ctx, rng), rng);
        }

        public static string EmoteSpam(ContentContext ctx, Random rng)
        {
            var count = 1 + rng.Next(4);
            var sb = new StringBuilder();
            var emote = RandomEmote(ctx, rng);
            for (var i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(' ');
                // A veces repite el mismo, a veces mezcla.
                sb.Append(rng.NextDouble() < 0.7 ? emote : RandomEmote(ctx, rng));
            }
            return sb.ToString();
        }

        // Solo emotes del canal (para el modo "solo emotes"). null si el canal no tiene.
        public static string? ChannelEmotesOnly(ContentContext ctx, Random rng)
        {
            if (ctx.ChannelEmotes.Count == 0) return null;
            var count = 1 + rng.Next(3);
            return string.Join(' ', Enumerable.Range(0, count).Select(_ => ctx.ChannelEmotes[rng.Next(ctx.ChannelEmotes.Count)]));
        }

        public static string Greeting(ContentContext ctx, Random rng) => Noise(Fill(Pick(Greetings, rng), ctx, rng), rng);
        public static string GreetBackTo(string user, ContentContext ctx, Random rng) => Noise(FillUser(Pick(GreetBack, rng), user, ctx, rng), rng, allowLower: false);
        public static string Answer(ContentContext ctx, Random rng) => Noise(Pick(Answers, rng), rng);
        public static string ReplyMention(string user, ContentContext ctx, Random rng) => FillUser(Pick(ReplyToMention, rng), user, ctx, rng);
        public static string ReplyQuestion(string user, ContentContext ctx, Random rng) => FillUser(Pick(ReplyToQuestion, rng), user, ctx, rng);
        public static string StreamerReaction(ContentContext ctx, Random rng) => Noise(Fill(Pick(StreamerReactions, rng), ctx, rng), rng);
        public static string Raid(string source, ContentContext ctx, Random rng) => Fill(Pick(RaidLines, rng).Replace("{source}", source), ctx, rng);
        public static string SanctionReaction(Random rng) => Pick(SanctionReactions, rng);
        public static string ClearReaction(Random rng) => Pick(ClearReactions, rng);
        public static string PollReaction(string option, ContentContext ctx, Random rng) => Noise(Fill(Pick(PollReactions, rng).Replace("{option}", option), ctx, rng), rng);
        public static string PollEndReaction(Random rng) => Pick(PollEndReactions, rng);
        public static string NewModReaction(string user, ContentContext ctx, Random rng) => FillUser(Pick(NewModReactions, rng), user, ctx, rng);
        public static string RaidWelcomeLine(string source, ContentContext ctx, Random rng) => Fill(Pick(RaidWelcome, rng).Replace("{source}", source), ctx, rng);
        public static string RaidOutgoingLine(string target, ContentContext ctx, Random rng) => Fill(Pick(RaidOutgoing, rng).Replace("{target}", target), ctx, rng);
        public static string StreamStartLine(ContentContext ctx, Random rng) => Noise(Fill(Pick(StreamStart, rng), ctx, rng), rng);
        public static string StreamEndLine(ContentContext ctx, Random rng) => Noise(Fill(Pick(StreamEnd, rng), ctx, rng), rng);
        public static string Copypasta(ContentContext ctx, Random rng) => Fill(Pick(Copypastas, rng), ctx, rng);
        public static string DonationMessage(ContentContext ctx, Random rng) => Fill(Pick(DonationMessages, rng), ctx, rng);

        // Mensaje de una "ola": el token con pequeñas variaciones (KEKW, KEKW KEKW, KEKWW).
        public static string WaveMessage(string token, Random rng)
        {
            var roll = rng.NextDouble();
            if (token.Length > 12) return token; // copypasta: se copia tal cual (la gracia es esa)
            if (roll < 0.45) return token;
            if (roll < 0.75) return string.Join(' ', Enumerable.Repeat(token, 2 + rng.Next(3)));
            if (roll < 0.9 && char.IsLetter(token[^1])) return token + new string(token[^1], 1 + rng.Next(4));
            return token + " " + Pick(GlobalEmotes, rng);
        }

        public static string RandomWaveToken(ContentContext ctx, Random rng)
        {
            var roll = rng.NextDouble();
            if (roll < 0.15) return Copypasta(ctx, rng);
            if (roll < 0.3 && ctx.ChannelEmotes.Count > 0) return ctx.ChannelEmotes[rng.Next(ctx.ChannelEmotes.Count)];
            return Pick(WaveTokens, rng);
        }

        // ── Utilidades ────────────────────────────────────────────────────

        public static T Pick<T>(IReadOnlyList<T> items, Random rng) => items[rng.Next(items.Count)];

        public static string RandomEmote(ContentContext ctx, Random rng) =>
            ctx.ChannelEmotes.Count > 0 && rng.NextDouble() < 0.5
                ? ctx.ChannelEmotes[rng.Next(ctx.ChannelEmotes.Count)]
                : Pick(GlobalEmotes, rng);

        public static string Fill(string template, ContentContext ctx, Random rng)
        {
            if (template.IndexOf('{') < 0) return template;
            var sb = new StringBuilder(template);
            sb.Replace("{streamer}", ctx.Streamer);
            sb.Replace("{category}", ctx.Category);
            sb.Replace("{city}", Pick(Cities, rng));
            sb.Replace("{n}", rng.Next(2, 100).ToString(CultureInfo.InvariantCulture));
            var text = sb.ToString();
            while (text.Contains("{emote}")) text = ReplaceFirst(text, "{emote}", RandomEmote(ctx, rng));
            if (text.Contains("{user}"))
            {
                var user = ctx.RandomChatter();
                text = user == null ? text.Replace("@{user} ", "").Replace("{user} ", "").Replace("{user}", ctx.Streamer) : text.Replace("{user}", user);
            }
            return text;
        }

        private static string FillUser(string template, string user, ContentContext ctx, Random rng) =>
            Fill(template.Replace("{user}", user), ctx, rng);

        private static string ReplaceFirst(string text, string search, string replacement)
        {
            var i = text.IndexOf(search, StringComparison.Ordinal);
            return i < 0 ? text : string.Concat(text.AsSpan(0, i), replacement, text.AsSpan(i + search.Length));
        }

        // Variaciones de escritura de una persona real (sin cambiar el sentido del mensaje).
        public static string Noise(string text, Random rng, bool allowLower = true)
        {
            if (text.Length == 0) return text;
            var roll = rng.NextDouble();
            if (allowLower && roll < 0.25) text = text.ToLowerInvariant();
            if (rng.NextDouble() < 0.2) text = RemoveAccents(text);
            if (rng.NextDouble() < 0.08 && char.IsLetter(text[^1])) text += new string(text[^1], 1 + rng.Next(3));
            if (rng.NextDouble() < 0.07) text += " " + Pick(GlobalEmotes, rng);
            if (rng.NextDouble() < 0.05) text += rng.NextDouble() < 0.5 ? " jaja" : " xd";
            return text;
        }

        public static string RemoveAccents(string text)
        {
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        public static string Normalize(string text) => RemoveAccents(text).ToLowerInvariant().Trim();
    }
}
