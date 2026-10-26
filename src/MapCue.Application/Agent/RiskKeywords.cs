using MapCue.Domain;

namespace MapCue.Application.Agent;

/// <summary>
/// A keyword or phrase of the risk classifier. Words are folded (<see cref="TextFold"/>); a trailing <c>*</c> on the last word
/// matches any word starting with it (<c>usuń*</c> matches <c>usuń</c>, <c>usunąć</c>, <c>usunięcie</c>).
/// </summary>
public sealed record RiskKeyword(string Text, RiskClass Risk)
{
    private readonly string[] _words = TextFold.Words(Text.TrimEnd('*'));
    private readonly bool _prefix = Text.EndsWith('*');

    public bool Matches(IReadOnlyList<string[]> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        foreach (var words in texts)
        {
            for (var start = 0; start + _words.Length <= words.Length; start++)
            {
                if (MatchesAt(words, start))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool MatchesAt(string[] words, int start)
    {
        for (var i = 0; i < _words.Length; i++)
        {
            var last = i == _words.Length - 1;
            var ok = last && _prefix
                ? words[start + i].StartsWith(_words[i], StringComparison.Ordinal)
                : string.Equals(words[start + i], _words[i], StringComparison.Ordinal);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Multilingual keywords (EN, PL, DE, FR, ES, IT, PT) per risk class (architecture §10.1 classes, R-06). Lists are deliberately
/// broad: a false positive skips an action, a false negative may execute a destructive one. Short stems that collide with
/// common navigation words (e.g. <c>pag*</c> and "page", <c>invest*</c> and an "Investments" tab) are listed as whole words.
/// </summary>
public static class RiskKeywords
{
    public static IReadOnlyList<RiskKeyword> All { get; } =
    [
        .. Of(RiskClass.StateChanging,
            "save", "submit", "confirm*", "apply", "accept*", "agree", "add", "create", "edit", "update", "enable", "disable",
            "turn on", "turn off", "approve", "sign up", "register", "log in", "login", "sign in", "upload*", "sync*", "rename",
            "archive*", "unsubscribe*", "clear", "done", "allow", "deny",
            "zapisz*", "zatwierdź*", "potwierdź*", "zastosuj", "akceptuj*", "zaakceptuj*", "zgadzam się", "dodaj*", "utwórz*",
            "edytuj*", "zmień*", "aktualizuj*", "włącz*", "wyłącz*", "zarejestruj*", "zaloguj*", "prześlij*", "synchronizuj*",
            "archiwizuj*", "zezwól*",
            "speichern", "bestätig*", "übernehm*", "hinzufüg*", "erstellen", "bearbeiten", "aktivieren", "deaktivieren",
            "akzeptieren", "anmelden", "registrieren", "zulassen", "erlauben",
            "enregistrer", "valider", "confirmer", "ajouter", "créer", "modifier", "activer", "désactiver", "accepter",
            "se connecter", "inscrire", "autoriser",
            "guardar", "confirmar", "aceptar", "agregar", "añadir", "crear", "editar", "activar", "desactivar", "registrarse",
            "iniciar sesión", "permitir",
            "salva", "conferma", "accetta", "aggiungi", "crea", "modifica", "attiva", "disattiva", "registrati", "accedi", "consenti",
            "salvar", "aceitar", "adicionar", "criar", "ativar", "desativar", "registrar", "entrar"),
        .. Of(RiskClass.Destructive,
            "delete*", "remove*", "erase*", "clear all", "clear data", "clear history", "reset*", "wipe*", "discard*", "uninstall*",
            "destroy*", "trash", "empty trash", "factory reset",
            "usuń*", "usuw*", "skasuj*", "kasuj*", "wyczyść*", "resetuj*", "zresetuj*", "odrzuć*", "wymaż*",
            "lösch*", "entfern*", "zurücksetz*", "verwerfen",
            "supprim*", "effac*", "réinitialis*",
            "elimin*", "borrar", "borra", "restablec*", "descartar",
            "cancella*", "rimuov*", "rimuovi",
            "exclu*", "apagar", "apague"),
        .. Of(RiskClass.Financial,
            "pay*", "transfer*", "buy*", "purchase*", "checkout", "check out", "subscribe*", "place order", "order now",
            "confirm order", "donate*", "top up", "withdraw*", "deposit*",
            "zapłać*", "płać", "płatność*", "przelew*", "przelej*", "kup", "kupuj*", "kupić", "zakup*", "zamów", "zamówić",
            "złóż zamówienie", "wpłać*", "wypłać*", "doładuj*", "subskrybuj*",
            "bezahlen", "zahlen", "kaufen", "jetzt kaufen", "überweis*", "bestellen",
            "payer", "acheter", "virement*", "commander",
            "pagar", "comprar", "transferir", "transferencia",
            "paga", "pagare", "acquista*", "compra", "bonifico",
            "pague"),
        .. Of(RiskClass.Communication,
            "send*", "share*", "call", "call now", "dial", "email", "e mail", "sms", "text message", "invite*", "publish*", "post",
            "reply*", "tweet*",
            "wyślij*", "wysłać", "wysył*", "udostępnij*", "zadzwoń*", "zaproś*", "opublikuj*", "odpowiedz",
            "senden", "absenden", "teilen", "anrufen", "einladen", "veröffentlich*",
            "envoy*", "partag*", "appeler", "inviter", "publier",
            "enviar", "compartir", "llamar", "invitar", "publicar",
            "invia*", "condividi*", "chiama*", "invita*", "pubblica*",
            "compartilhar", "partilhar", "ligar", "convidar"),
        .. Of(RiskClass.AccountChange,
            "log out", "logout", "sign out", "signout", "log off", "change password", "reset password", "delete account",
            "close account", "deactivate*", "change email", "change pin", "unlink*", "disconnect*",
            "wyloguj*", "zmień hasło", "zmień pin", "zamknij konto", "usuń konto", "dezaktywuj*", "odłącz*",
            "abmelden", "passwort ändern", "konto löschen", "konto schließen",
            "déconnexion", "déconnecter", "changer le mot de passe", "supprimer le compte",
            "cerrar sesión", "cambiar contraseña", "eliminar cuenta",
            "esci", "disconnetti*", "cambia password", "elimina account",
            "sair", "terminar sessão", "alterar senha", "excluir conta"),
        .. Of(RiskClass.External,
            "open in browser", "open link", "visit website", "website", "rate us", "rate app", "rate this app", "app store",
            "play store", "google play", "download app", "http", "https", "www",
            "otwórz w przeglądarce", "oceń aplikację", "odwiedź stronę", "strona www",
            "im browser öffnen", "app bewerten", "webseite besuchen",
            "ouvrir dans le navigateur", "noter l'application", "visiter le site",
            "abrir en el navegador", "calificar*", "visitar sitio",
            "apri nel browser", "valuta l'app", "visita il sito",
            "abrir no navegador", "avaliar*", "visitar site"),
    ];

    private static IEnumerable<RiskKeyword> Of(RiskClass risk, params string[] texts) => texts.Select(t => new RiskKeyword(t, risk));
}
