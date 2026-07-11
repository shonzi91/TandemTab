using Microsoft.JSInterop;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// Minimal UI localization. English text is the lookup key, so untranslated strings fall back to
/// English automatically and there are no separate key names to maintain — only one Bulgarian map.
/// The chosen language is persisted in the browser's localStorage. Components inject this, render
/// <c>Loc.T("English")</c>, and re-render on <see cref="Changed"/>.
/// </summary>
public sealed class Localizer(IJSRuntime js)
{
    private const string StorageKey = "finapp-lang";

    /// <summary>Supported UI languages (code, display name, flag). Add a row here + a Bg-style map to add a language.</summary>
    public static readonly IReadOnlyList<(string Code, string Name, string Flag)> Languages =
    [
        ("en", "English", "🇬🇧"),
        ("bg", "Български", "🇧🇬"),
    ];

    private static bool IsSupported(string? code) => code is not null && Languages.Any(l => l.Code == code);

    public string Culture { get; private set; } = "en";

    /// <summary>The display name of the currently-selected language.</summary>
    public string CultureName => Languages.FirstOrDefault(l => l.Code == Culture).Name ?? Culture;

    /// <summary>The flag of the currently-selected language.</summary>
    public string CultureFlag => Languages.FirstOrDefault(l => l.Code == Culture).Flag ?? "🌐";
    public event Action? Changed;

    /// <summary>Load the saved language once at startup (call from the layout's OnInitializedAsync).</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var saved = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (IsSupported(saved) && saved != Culture)
            {
                Culture = saved;
                Changed?.Invoke();
            }
        }
        catch { /* storage unavailable — stay on English */ }
    }

    public async Task SetCultureAsync(string culture)
    {
        if (!IsSupported(culture) || culture == Culture) return;
        Culture = culture;
        try { await js.InvokeVoidAsync("localStorage.setItem", StorageKey, culture); }
        catch { /* ignore */ }
        Changed?.Invoke();
    }

    /// <summary>Translate <paramref name="en"/> to the current culture, falling back to the English text.</summary>
    public string T(string en) => Culture == "bg" && Bg.TryGetValue(en, out var v) ? v : en;

    public string this[string en] => T(en);

    // English -> Bulgarian. Keys are the exact English strings rendered in the UI.
    private static readonly Dictionary<string, string> Bg = new(StringComparer.Ordinal)
    {
        // App chrome
        ["Hello,"] = "Здравей,",
        ["Sign out"] = "Изход",
        ["Profile settings"] = "Настройки на профила",
        ["Change password"] = "Смяна на парола",
        ["Current password"] = "Текуща парола",
        ["New password"] = "Нова парола",
        ["Confirm new password"] = "Потвърди новата парола",
        ["Password changed."] = "Паролата е сменена.",
        ["The new passwords don’t match."] = "Новите пароли не съвпадат.",
        ["Loading…"] = "Зареждане…",
        ["Saving…"] = "Запазване…",
        ["Dismiss"] = "Затвори",
        ["Couldn’t do that."] = "Неуспешно действие.",

        // Auth
        ["Private, shared budgeting. Sign in or create an account to begin."] =
            "Личен, споделен бюджет. Влез или създай профил, за да започнеш.",
        ["Track together, save together."] = "Следете заедно, спестявайте заедно.",
        ["Simple family goals, zero stress. Sign in or create an account to begin."] =
            "Прости семейни цели, нула стрес. Влез или създай профил, за да започнеш.",
        ["Sign in"] = "Вход",
        ["Create account"] = "Създай профил",
        ["or"] = "или",
        ["Continue with Google"] = "Влез с Google",
        ["Continue with Facebook"] = "Влез с Facebook",
        ["Username or email"] = "Потребител или имейл",
        ["Password"] = "Парола",
        ["Username"] = "Потребител",
        ["Email"] = "Имейл",
        ["you@example.com or username"] = "you@example.com или потребител",
        ["Your password"] = "Твоята парола",
        ["Pick a username"] = "Избери потребителско име",
        ["At least 8 characters"] = "Поне 8 символа",
        ["Signing in…"] = "Влизане…",
        ["Creating…"] = "Създаване…",
        ["Password must be at least 8 characters."] = "Паролата трябва да е поне 8 символа.",
        ["Couldn’t reach the server. Check your connection and try again."] =
            "Сървърът е недостъпен. Провери връзката и опитай отново.",

        // Brand
        ["Budget like a budgie."] = "Бюджет, лек като перце.",

        // First run
        ["Welcome to TandemTab"] = "Добре дошъл в TandemTab",
        ["Let’s get you rolling. Create your first account to get started (e.g. Personal, Shared, Family)."] =
            "Да потегляме. Създай първия си профил, за да започнеш (напр. Личен, Споделен, Семеен).",
        ["Off balance — overspent by"] = "Извън баланс — преразход с",
        ["Expenses ate into your savings earmark. This will need to be covered next period (from a savings bucket or fresh contributions)."] =
            "Разходите изядоха заделените спестявания. Това трябва да се покрие следващия период (от спестовна каса или нови вноски).",
        ["Account name"] = "Име на профил",
        ["Currency"] = "Валута",
        ["It starts with a few starter categories and the current month’s period — you can change everything."] =
            "Започва с няколко начални категории и периода за текущия месец — всичко може да се променя.",

        // Tabs
        ["Account"] = "Профил",
        ["Budgets"] = "Бюджети",
        ["Expenses"] = "Разходи",
        ["Savings"] = "Спестявания",
        ["Debt/Savings"] = "Дългове/Спестявания",
        ["Insights"] = "Анализи",
        ["Overview"] = "Преглед",
        ["Home"] = "Начало",
        ["Spending"] = "Разходи",
        ["Goals"] = "Цели",
        ["Wallets"] = "Портфейли",
        ["Setup"] = "Настройки",
        ["This month's budgets"] = "Бюджети за месеца",
        ["People"] = "Хора",
        ["Invite"] = "Покани",
        ["Privacy"] = "Поверителност",
        ["Terms"] = "Условия",
        ["Before you start"] = "Преди да започнете",
        ["To use TandemTab, please review and accept our terms."] = "За да използвате TandemTab, моля прегледайте и приемете условията.",
        ["I have read and accept the Terms of Service and Privacy Policy."] = "Прочетох и приемам Общите условия и Политиката за поверителност.",
        ["Accept & continue"] = "Приеми и продължи",
        ["I understand and consent."] = "Разбирам и давам съгласие.",
        ["Consent & continue"] = "Съгласие и продължи",
        ["Connect your bank"] = "Свържете банката си",
        ["You authorize TandemTab to connect to your bank through our regulated provider (Enable Banking) and to read your account details and transactions on a read-only basis, to show them here. No payments are ever made. You can withdraw this at any time by disconnecting."] = "Упълномощавате TandemTab да се свърже с банката ви чрез нашия лицензиран доставчик (Enable Banking) и да чете данните за сметката и транзакциите ви само за четене, за да ги показва тук. Не се извършват плащания. Можете да оттеглите това по всяко време чрез прекъсване.",
        ["Sync this fund with your bank"] = "Синхронизирайте този фонд с банката си",
        ["You authorize TandemTab to treat this fund as a mirror of your linked bank account: imported transactions post here and its balance follows the real account instead of your manual entries. You can withdraw this at any time by unsyncing the fund."] = "Упълномощавате TandemTab да третира този фонд като огледало на свързаната ви банкова сметка: импортираните транзакции влизат тук, а балансът следва реалната сметка вместо ръчните ви записи. Можете да оттеглите това по всяко време, като десинхронизирате фонда.",
        ["you"] = "вие",
        ["owner"] = "собственик",
        ["Remove from account"] = "Премахни от акаунта",
        ["Leave account"] = "Напусни акаунта",
        ["Leave"] = "Напусни",
        ["Remove"] = "Премахни",
        ["New owner"] = "Нов собственик",
        ["Choose a member…"] = "Изберете член…",
        ["Archive account"] = "Архивирай акаунта",
        ["{0} will lose access to this account. Their recorded contributions and expenses stay."] = "{0} ще загуби достъп до този акаунт. Записаните вноски и разходи остават.",
        ["You're the only person here, so the account will be archived for 30 days. You can restore it from your profile before then; after that it's deleted."] = "Вие сте единственият тук, затова акаунтът ще бъде архивиран за 30 дни. Можете да го възстановите от профила си дотогава; след това се изтрива.",
        ["You own this account, so hand it to another member before you go."] = "Вие сте собственик на този акаунт, затова го предайте на друг член, преди да напуснете.",
        ["You'll lose access to this account. Your recorded contributions and expenses stay for the others."] = "Ще загубите достъп до този акаунт. Записаните ви вноски и разходи остават за останалите.",
        ["Archived accounts"] = "Архивирани акаунти",
        ["Archived accounts are deleted after 30 days. Restore one to bring it back."] = "Архивираните акаунти се изтриват след 30 дни. Възстановете, за да върнете акаунт.",
        ["Restore"] = "Възстанови",
        ["{0} days left"] = "остават {0} дни",
        ["Bank sync"] = "Банково синхронизиране",
        ["Bank"] = "Банка",
        ["External accounts"] = "Външни сметки",
        ["Each account links its own bank."] = "Всеки акаунт свързва собствена банка.",
        ["No transactions waiting for this period. Hit Refresh to check for new ones."] = "Няма чакащи транзакции за този период. Натиснете Обнови, за да проверите за нови.",
        ["Refresh"] = "Обнови",
        ["Link Revolut"] = "Свържи Revolut",
        ["Link your Revolut account to pull transactions in automatically."] = "Свържете акаунта си в Revolut, за да се изтеглят транзакциите автоматично.",
        ["Reconnect"] = "Свържи отново",
        ["last synced"] = "последно синхр.",
        ["money in"] = "приход",
        ["Review each transaction, pick a category and fund, then add it as an expense."] = "Прегледайте всяка транзакция, изберете категория и фонд, след което я добавете като разход.",
        ["No transactions waiting. Hit Refresh to check for new ones."] = "Няма чакащи транзакции. Натиснете Обнови, за да проверите за нови.",
        ["Reopen the period to import transactions."] = "Отворете периода отново, за да импортирате транзакции.",
        ["Pick a category and fund first."] = "Първо изберете категория и фонд.",
        ["Pick a category first."] = "Първо изберете категория.",
        ["Posts to your synced fund"] = "Отива в синхронизирания ви фонд",
        ["No expenses waiting for this period."] = "Няма чакащи разходи за този период.",
        ["Incoming from bank"] = "Постъпления от банката",
        ["From… (source)"] = "От… (източник)",
        ["Into your synced fund"] = "Към синхронизирания ви фонд",
        ["Add movement"] = "Добави движение",
        ["Always use this source for this merchant"] = "Използвай този източник за този търговец занапред",
        ["Pick where this money came from first."] = "Първо изберете откъде дойдоха тези пари.",
        ["Mark a fund as synced to this bank (Setup → a fund → Edit) so imports file automatically."] = "Отбележете фонд като синхронизиран с тази банка (Настройки → фонд → Редакция), за да се завеждат импортите автоматично.",
        ["Mark a fund as synced to this bank (Edit a fund) to receive these."] = "Отбележете фонд като синхронизиран с тази банка (Редакция на фонд), за да ги получавате.",
        ["Disconnect"] = "Прекъсни",
        ["Always use this pick for this merchant"] = "Използвай този избор за този търговец занапред",
        ["Auto-fills {0} — click to forget"] = "Автоматично попълва {0} — щракнете, за да забравите",
        ["Map to… (optional)"] = "Свържи с… (по избор)",
        ["Money in"] = "Приходи",
        ["From your bank"] = "От банката ви",
        ["Transfer this money-out"] = "Прехвърли този разход",
        ["Record transfer"] = "Запиши прехвърляне",
        ["Record as a transfer to another fund/account"] = "Запиши като прехвърляне към друг фонд/акаунт",
        ["No imported expenses on this day."] = "Няма импортирани разходи за този ден.",
        ["Imported expenses are reviewed in the Spending tab; money-in under Funds → Move money."] = "Импортираните разходи се преглеждат в раздел Разходи; приходите — в Сметки → Премести пари.",
        ["Fetch new transactions"] = "Изтегли нови транзакции",
        ["Looks like you already logged this: {0} · {1} {2} · {3} · {4}"] = "Изглежда вече сте записали това: {0} · {1} {2} · {3} · {4}",
        ["Same — replace"] = "Същото — замени",
        ["Keep both"] = "Запази двете",
        ["Mark a fund as synced to this bank (a fund → Edit) so imports file automatically."] = "Отбележете фонд като синхронизиран с тази банка (фонд → Редакция), за да се завеждат импортите автоматично.",
        ["Auto-categorized merchants"] = "Автоматично категоризирани търговци",
        ["Imported transactions from these are filed here automatically. Unmapping only stops future auto-filing."] = "Импортираните транзакции от тях се завеждат тук автоматично. Премахването спира само бъдещото авт. завеждане.",
        ["Unmap"] = "Премахни",
        ["Synced with a linked bank account"] = "Синхронизиран със свързана банкова сметка",
        ["Sync this fund with {0} (linked account)"] = "Синхронизирай този фонд с {0} (свързана сметка)",
        ["Sync this fund with {0}"] = "Синхронизирай този фонд с {0}",
        ["Bank account"] = "Банкова сметка",
        ["Map a fund to one of this bank's accounts by editing the fund and turning on sync."] = "Свържете фонд с една от сметките на тази банка, като редактирате фонда и включите синхронизация.",
        ["Link a bank first (Money → Bank sync) to sync a fund with it."] = "Първо свържете банка (Пари → Банково синхронизиране), за да синхронизирате фонд с нея.",
        ["Bank imports post here, and its balance mirrors the real account — expenses, transfers and deposits won’t change it (only affects entries created from now on)."] = "Банковите импорти влизат тук, а балансът отразява реалната сметка — разходи, преводи и вноски няма да го променят (важи само за записи отсега нататък).",
        ["Link a bank in the Bank tab to sync a fund with it."] = "Свържете банка в раздела Банка, за да синхронизирате фонд с нея.",
        ["Synced with your bank — managed automatically"] = "Синхронизиран с банката ви — управлява се автоматично",
        ["Synced with {0}"] = "Синхронизиран с {0}",
        ["Live account balance"] = "Актуален баланс на сметката",
        ["Live bank balance"] = "Актуален банков баланс",
        ["Choose bank account"] = "Изберете банкова сметка",
        ["Expenses, transfers and deposits won’t change this fund’s balance — the bank’s real balance is authoritative. Only affects entries created from now on."] = "Разходи, преводи и вноски няма да променят баланса на този фонд — реалният банков баланс е меродавен. Важи само за записи, създадени отсега нататък.",
        ["Pick something to map to first."] = "Първо изберете към какво да свържете.",
        ["Where your money is"] = "Къде са парите ви",
        ["Move money"] = "Премести пари",
        ["Income"] = "Приходи",
        ["free"] = "свободни",
        ["Money"] = "Пари",
        ["Actions"] = "Действия",
        ["Transfer"] = "Прехвърли",
        ["imported"] = "импортиран",
        ["of"] = "от",
        ["spent"] = "похарчени",
        ["Trends, savings rate & score"] = "Тенденции, норма на спестяване и оценка",

        // Overview tab
        ["Health score"] = "Оценка на здравето",
        ["Health score & trends"] = "Оценка на здравето и тенденции",
        ["How your score works: four equal parts (25 pts each) — how much you saved vs your target, how well you kept to budget, living within your income, and your spending vs your recent average."] =
            "Как се формира оценката: четири равни части (по 25 т.) — колко спестихте спрямо целта, колко се придържахте към бюджета, дали живеете според доходите си и разходите спрямо скорошната ви средна стойност.",
        ["Needs your attention"] = "Изисква внимание",
        ["All clear — no warnings this period. Nice work."] = "Всичко е наред — няма предупреждения този период. Браво!",
        ["Top spending"] = "Най-големи разходи",
        ["Log some income or expenses to see your account overview here."] =
            "Въведете приходи или разходи, за да видите преглед на профила тук.",
        ["Overspent budgets"] = "Преразходени бюджети",
        ["avg"] = "ср.",
        ["above average"] = "над средното",
        ["below average"] = "под средното",

        // Modal labels, titles, hints & tooltips (Session 12e translation pass)
        ["From"] = "От",
        ["Budget amount"] = "Сума на бюджета",
        ["Alert at %"] = "Предупреждение при %",
        ["Goal amount (optional)"] = "Целева сума (по избор)",
        ["Remove expense"] = "Премахни разхода",
        ["Add a fund"] = "Добави фонд",
        ["Delete account"] = "Изтрий профила",
        ["New savings bucket"] = "Нов спестовен джоб",
        ["Budget for this period (optional)"] = "Бюджет за този период (по избор)",
        ["Already saved (starting balance)"] = "Вече спестено (начален баланс)",
        ["Move balance to"] = "Премести баланса към",
        ["Notify on milestone"] = "Известявай при достигане на цел",
        ["Notify on every expense"] = "Известявай при всеки разход",
        ["Money you already had in this bucket before using FinApp. It counts toward the balance and goal, but not toward your savings rate."] =
            "Пари, които вече сте имали в този джоб преди да ползвате FinApp. Броят се към баланса и целта, но не и към нормата на спестяване.",
        ["Edit savings deposit —"] = "Редактирай спестяване —",
        ["Undo this savings movement?"] = "Да отмените ли това движение по спестяванията?",
        ["Remove those first."] = "Първо премахнете тях.",
        ["Can’t delete —"] = "Не може да се изтрие —",
        ["Can’t remove —"] = "Не може да се премахне —",
        ["Spend or move its savings first."] = "Първо похарчете или преместете спестяванията му.",
        ["This removes the empty bucket permanently."] = "Това премахва празния джоб завинаги.",
        ["This removes the fund permanently."] = "Това премахва фонда завинаги.",
        ["This fund has an opening balance. Move it to another fund, or remove it as-is (the balance is dropped)."] =
            "Този фонд има начален баланс. Преместете го към друг фонд или го премахнете така (балансът се губи).",
        ["Later periods shift to stay contiguous, keeping their own lengths."] =
            "Следващите периоди се изместват, за да останат последователни, запазвайки дължините си.",
        ["This permanently deletes the account and"] = "Това изтрива завинаги профила и",
        ["all its periods, budgets, expenses and savings"] = "всички негови периоди, бюджети, разходи и спестявания",
        ["This can't be undone."] = "Това не може да бъде отменено.",
        ["This deletes period"] = "Това изтрива период",
        ["and everything in it, then re-opens the previous period as active."] =
            "и всичко в него, след което активира отново предишния период.",
        ["Enter what each fund really holds now (previous closing balance:"] =
            "Въведете колко реално има всеки фонд сега (предишен краен баланс:",
        ["These become the new period's opening balances — that money carries over and is fully available to budget or save."] =
            "Те стават началните баланси на новия период — тези пари се прехвърлят и са напълно достъпни за бюджет или спестяване.",
        ["Enter the username of an existing FinApp user. They'll get a prompt to accept; once they do, they can edit everything except deleting the account."] =
            "Въведете потребителското име на съществуващ потребител. Той ще получи покана; след като я приеме, може да редактира всичко освен изтриването на профила.",
        ["Remove period"] = "Премахни периода",
        ["— don’t move —"] = "— не премествай —",
        ["Undo"] = "Отмени",
        ["Add a new account"] = "Добави нов профил",
        ["Invite a contributor"] = "Покани сътрудник",
        ["Remove this period and reopen the previous one"] = "Премахни този период и активирай предишния",
        ["Add a top-level category"] = "Добави основна категория",
        ["Add a savings bucket"] = "Добави спестовен джоб",
        ["Remove transfer"] = "Премахни прехвърлянето",
        ["Remove this transfer (does not reverse the deposit in the other account)"] =
            "Премахни това прехвърляне (не отменя депозита в другия профил)",
        ["You were invited to this account"] = "Бяхте поканени в този профил",
        ["Previous period"] = "Предишен период",
        ["Next period"] = "Следващ период",
        ["Sum of the period's opening fund values"] = "Сбор от началните стойности на фондовете за периода",
        ["Add bucket"] = "Добави джоб",
        ["Account actions"] = "Действия с профила",
        ["to"] = "към",

        // Insights tab — generated narrative, signals, trend, quick wins (format strings keep their {0}, {1}… slots)
        ["You're up {0} points from last month."] = "Нагоре с {0} точки спрямо миналия месец.",
        ["You're down {0} points from last month."] = "Надолу с {0} точки спрямо миналия месец.",
        ["Looking healthy"] = "Изглежда здравословно",
        ["Your habits are solid — saving steadily, spending within plan."] = "Навиците ви са стабилни — спестявате редовно и харчите по план.",
        ["Getting there"] = "На прав път",
        ["Solid foundations, but a couple of habits are dragging you down. Tighten one area and next month could look very different."] =
            "Добра основа, но няколко навика ви дърпат надолу. Стегнете една област и следващият месец може да изглежда съвсем различно.",
        ["Needs attention"] = "Изисква внимание",
        ["A few things need a look this period — overspending or thin savings. Small fixes add up fast."] =
            "Няколко неща се нуждаят от внимание този период — преразход или слаби спестявания. Малките корекции бързо се натрупват.",
        ["Not enough history yet to spot a trend."] = "Все още няма достатъчно история за тенденция.",
        ["This month is right around your {0}-month average of {1}."] = "Този месец е около средното ви за {0} месеца от {1}.",
        ["This month is {0} above your {1}-month average of {2}."] = "Този месец е с {0} над средното ви за {1} месеца от {2}.",
        ["This month is {0} below your {1}-month average of {2}."] = "Този месец е с {0} под средното ви за {1} месеца от {2}.",
        ["{0} is running high"] = "{0} е завишен",
        ["You've spent {0} on {1} — {2} ({3}%) above your recent average of {4}."] =
            "Похарчили сте {0} за {1} — {2} ({3}%) над скорошното ви средно от {4}.",
        ["No savings set aside"] = "Няма заделени спестявания",
        ["You haven't moved anything into savings this period. Even a small amount keeps the habit alive."] =
            "Не сте заделили нищо за спестявания този период. Дори малка сума поддържа навика.",
        ["Savings on track"] = "Спестяванията са в час",
        ["You set aside {0} of what came in — at or above your {1} goal."] = "Заделили сте {0} от постъпленията — на или над целта ви от {1}.",
        ["{0} spend down"] = "По-малко разходи за {0}",
        ["{0} vs {1} last month. Keep it up."] = "{0} спрямо {1} миналия месец. Продължавайте така.",
        ["Days left in the period"] = "Оставащи дни в периода",
        ["You have {0} on hand with {1} days to go."] = "Разполагате с {0} при оставащи {1} дни.",
        ["{0}d left"] = "{0}д остават",
        ["Spending dipped into savings"] = "Разходите навлязоха в спестяванията",
        ["{0} of this period's spend isn't backed by fresh cash — it leans on your savings earmark."] =
            "{0} от разходите за този период не са покрити с нови пари — разчитат на заделените спестявания.",
        ["that category"] = "тази категория",
        ["Rein in {0}: you're {1} over budget this month."] = "Ограничете {0}: с {1} над бюджета този месец.",
        ["Set aside {0} more to hit your {1} savings goal."] = "Заделете още {0}, за да достигнете целта си за спестяване от {1}.",
        ["Give {0} a budget — you've spent {1} with no plan in place."] = "Задайте бюджет за {0} — похарчили сте {1} без план.",
        ["No contributions recorded this period, so there's no savings rate to measure yet."] =
            "Няма записани вноски за този период, така че още няма норма на спестяване.",
        ["You saved {0} this period — at or above your {1} goal. Keep that rhythm."] =
            "Спестихте {0} този период — на или над целта ви от {1}. Запазете темпото.",
        ["That's about {0} short of your goal this period."] = "Това е около {0} под целта ви за този период.",
        ["You saved {0} this period — better than nothing, but short of your {1} goal."] =
            "Спестихте {0} този период — по-добре от нищо, но под целта ви от {1}.",

        // Insights / financial-health tab
        ["Your score this period"] = "Вашата оценка за периода",
        ["out of 100"] = "от 100",
        ["At risk"] = "Рисково",
        ["Average"] = "Средно",
        ["Healthy"] = "Здравословно",
        ["This period's signals"] = "Сигнали за периода",
        ["Where it's going"] = "Накъде отиват парите",
        ["Savings rate"] = "Норма на спестяване",
        ["Target:"] = "Цел:",
        ["Goal"] = "Цел",
        ["Spending trend"] = "Тенденция на разходите",
        ["Outgoings"] = "Разходи",
        ["trending up"] = "във възход",
        ["trending down"] = "в спад",
        ["Quick wins"] = "Бързи победи",
        ["Once you've logged some income or expenses, your financial-health report shows up here."] =
            "След като въведете приходи или разходи, тук ще се появи отчетът за финансовото ви здраве.",
        ["Icon"] = "Икона",
        ["Auto (from name)"] = "Автоматично (по име)",
        ["Language"] = "Език",
        ["Profile picture"] = "Профилна снимка",
        ["Appearance"] = "Облик",
        ["Dark theme"] = "Тъмна тема",
        ["Sign-in"] = "Вход",
        ["You sign in with {0} — there's no password to manage."] = "Влизате чрез {0} — няма парола за управление.",
        ["Upload"] = "Качи",
        ["Stored on this device only."] = "Запазва се само на това устройство.",
        ["Sub-categories"] = "Подкатегории",
        ["Savings target (%)"] = "Цел за спестяване (%)",
        ["Edit account"] = "Редактирай профила",
        ["Your monthly savings goal — drives the Insights score."] =
            "Месечната ви цел за спестяване — определя оценката в Анализи.",
        ["can't be changed once an account exists."] = "не може да се променя след създаване на профила.",

        // Account-tab cards + balances
        ["Current"] = "Текущо",
        ["Closed on"] = "Затворено с",
        ["Spent"] = "Похарчено",
        ["Budgeted"] = "Бюджетирано",
        ["Saved this period"] = "Спестено този период",
        ["of contributions"] = "от вноските",
        ["Opening"] = "Начално",
        ["Active"] = "Активен",
        ["Closed"] = "Затворен",
        ["shared"] = "споделен",

        // Invitations
        ["You’re invited"] = "Имаш покана",
        ["pending invitation"] = "чакаща покана",
        ["pending invitations"] = "чакащи покани",
        ["invited you to"] = "те покани в",
        ["Accept"] = "Приеми",
        ["Decline"] = "Откажи",

        // Panels / headings
        ["Funds"] = "Сметки",
        ["Transfer money"] = "Прехвърли пари",
        ["Other accounts"] = "Други профили",
        ["Move money between your funds — the total is unchanged, only where it sits."] =
            "Премести пари между сметките си — общата сума не се променя, само къде стои.",
        ["Sending to another account leaves this one as an outflow."] =
            "Изпращането към друг профил напуска този като изходящо.",
        ["Available to send:"] = "Налично за изпращане:",
        ["cash not earmarked for savings"] = "пари, незаделени за спестявания",
        ["not backed by cash"] = "непокрити с налични пари",
        ["Category & fund"] = "Категория и сметка",
        ["New fund"] = "Нова сметка",
        ["Transfer from this fund"] = "Прехвърли от тази сметка",
        ["Deposit to this fund"] = "Внеси в тази сметка",
        ["Transfer from"] = "Прехвърли от",
        ["To"] = "Към",
        ["Available in this fund:"] = "Налично в тази сметка:",
        ["what this fund holds, not earmarked for savings"] = "каквото е в сметката, незаделено за спестявания",
        ["Opening balance this period"] = "Начален баланс този период",
        ["Opening balance this period (optional)"] = "Начален баланс този период (по избор)",
        ["Manage categories"] = "Управление на категории",
        ["Add a deposit"] = "Добави вноска",
        ["Contribution categories"] = "Категории вноски",
        ["Edit"] = "Редактирай",
        ["Destination"] = "Получател",
        ["Move"] = "Премести",
        ["Spend"] = "Похарчи",
        ["Goals activity"] = "Дейност по цели",
        ["Available to save:"] = "Налично за спестяване:",
        ["the money in the account, minus what's budgeted and already saved"] =
            "парите в профила минус бюджетираното и вече спестеното",
        ["A budget matures the saving into this month's plan; another bucket just shifts it across. The source bucket drops either way."] =
            "Бюджет превръща спестяването в план за този месец; друга каса просто го прехвърля. Касата източник намалява и в двата случая.",
        ["Contributions"] = "Вноски",
        ["Add expense"] = "Добави разход",
        ["All expenses"] = "Всички разходи",
        ["Savings buckets"] = "Спестовни каси",
        ["Add to savings"] = "Добави към спестявания",
        ["Move it to the loan"] = "Насочи към заема",
        ["Move budget to your debt"] = "Насочи бюджет към дълга",
        ["Move {0} from your {1} budget toward {2}?"] = "Да насоча ли {0} от бюджета „{1}“ към {2}?",
        ["{0} budget trimmed to {1}"] = "Бюджетът „{0}“ намален до {1}",
        ["{0} set aside toward {1}"] = "{0} заделени към {1}",
        ["This moves real money into your debt earmark and lowers this budget. You can change both later."] =
            "Това заделя реални пари към дълга и намалява този бюджет. Можете да промените и двете по-късно.",
        ["Move it"] = "Насочи",
        ["Recurring"] = "Повтарящи се",
        ["Add recurring"] = "Добави повтарящо се",
        ["Bucket"] = "Кофичка",
        ["Import statement"] = "Импортирай извлечение",
        ["Import a bank statement (Excel, CSV, XML, OFX, QIF)"] = "Импортирай банково извлечение (Excel, CSV, XML, OFX, QIF)",
        ["Export a statement from your bank (Excel, CSV, XML, OFX or QIF) and upload it here. It's parsed on your device — nothing is sent until you confirm the rows."] =
            "Свали извлечение от банката си (Excel, CSV, XML, OFX или QIF) и го качи тук. Обработва се на устройството ти — нищо не се изпраща, докато не потвърдиш редовете.",
        ["Money out is negative, money in positive."] = "Разходите са отрицателни, приходите — положителни.",
        ["Tip: if your bank only offers a PDF, look for a \"download/export transactions\" option — it usually also offers Excel or CSV, which import cleanly."] =
            "Съвет: ако банката ти дава само PDF, потърси опция „свали/експортирай транзакции“ — обикновено предлага и Excel или CSV, които се импортират чисто.",
        ["Tell us which columns to use."] = "Посочи кои колони да използваме.",
        ["Separate money-in / money-out columns"] = "Отделни колони за постъпления / разходи",
        ["Money out (debit)"] = "Разход (дебит)",
        ["Money in (credit)"] = "Постъпление (кредит)",
        ["Preview"] = "Преглед",
        ["{0} of {1} rows will import into this period. Uncheck anything you don't want."] =
            "{0} от {1} реда ще се импортират в този период. Махни отметката на тези, които не искаш.",
        ["(no description)"] = "(без описание)",
        ["outside period"] = "извън периода",
        ["Looks already logged"] = "Изглежда вече е вписано",
        ["Import {0}"] = "Импортирай {0}",
        ["That file looks empty."] = "Файлът изглежда празен.",
        ["Couldn't recognise that file. Use a CSV, OFX or QIF export."] = "Файлът не е разпознат. Използвай CSV, OFX или QIF.",
        ["Couldn't read that file."] = "Файлът не можа да се прочете.",
        ["No transactions found in that file."] = "Във файла няма намерени транзакции.",
        ["External"] = "Външна",
        ["Edit recurring"] = "Редактирай повтарящо се",
        ["Bills, salary and standing transfers that repeat monthly. They remind you when due — you confirm the real amount, so bills that vary stay accurate."] =
            "Сметки, заплата и постоянни преводи, които се повтарят месечно. Напомнят ти при падеж — ти потвърждаваш реалната сума, така че променливите сметки остават точни.",
        ["Nothing recurring yet. Add your rent, salary or a monthly bill."] =
            "Още няма повтарящи се. Добави наем, заплата или месечна сметка.",
        ["reminder only"] = "само напомняне",
        ["day"] = "ден",
        ["paused"] = "на пауза",
        ["Bill"] = "Сметка",
        ["Income"] = "Доход",
        ["Fixed"] = "Фиксирана",
        ["Typical"] = "Обичайна",
        ["Reminder only"] = "Само напомняне",
        ["Typical amount"] = "Обичайна сума",
        ["An estimate that self-tunes toward what you actually pay."] =
            "Приблизителна сума, която сама се настройва към това, което реално плащаш.",
        ["The same amount every month."] = "Една и съща сума всеки месец.",
        ["No amount — you'll enter the real figure each time it's due (good for a variable salary)."] =
            "Без сума — въвеждаш реалната всеки път при падеж (удобно за променлива заплата).",
        ["Day of month"] = "Ден от месеца",
        ["Confirm income"] = "Потвърди дохода",
        ["Confirm bill"] = "Потвърди сметката",
        ["Enter what actually came in or went out."] = "Въведи какво реално влезе или излезе.",
        ["Expected about {0}. Adjust to the real amount if it differs."] =
            "Очаквано около {0}. Коригирай към реалната сума, ако се различава.",
        ["Actual amount"] = "Реална сума",
        ["{0} is due — enter the amount."] = "{0} е с падеж — въведи сумата.",
        ["{0} is due (about {1}) — confirm it."] = "{0} е с падеж (около {1}) — потвърди.",
        ["bills due"] = "предстоящи сметки",
        ["Recurring bills expected this period that you haven't logged yet"] =
            "Повтарящи се сметки, очаквани този период, които още не си вписал",
        ["tomorrow"] = "утре",
        ["in {0} days"] = "след {0} дни",
        ["{0} (about {1}) is due {2}."] = "{0} (около {1}) е с падеж {2}.",
        ["{0} is due {1}."] = "{0} е с падеж {1}.",
        ["Post automatically when due (don't ask me)"] = "Публикувай автоматично при падеж (без питане)",
        ["auto"] = "авто",
        ["{0} ({1}) was posted automatically."] = "{0} ({1}) беше публикувано автоматично.",
        ["Collapse"] = "Свий",
        ["Budget savings"] = "Бюджетирай спестявания",
        ["Spend savings"] = "Похарчи спестявания",
        // Typed buckets (common vs debt-payoff) + the Debts section
        ["Type"] = "Тип",
        ["Savings goal"] = "Спестовна цел",
        ["Debt payoff"] = "Погасяване на дълг",
        // Investment buckets (#3)
        ["Investment"] = "Инвестиция",
        ["Investments"] = "Инвестиции",
        ["Growth projection"] = "Прогноза за растеж",
        ["Withdraw"] = "Изтегли",
        ["invested"] = "инвестирани",
        ["Compounding"] = "Олихвяване",
        ["Monthly"] = "Месечно",
        ["Quarterly"] = "Тримесечно",
        ["Yearly"] = "Годишно",
        ["Daily"] = "Дневно",
        ["Expected return (% / year)"] = "Очаквана доходност (% / год.)",
        ["Horizon (years)"] = "Хоризонт (години)",
        ["Add an investment"] = "Добави инвестиция",
        ["Add investment"] = "Добави инвестиция",
        ["Invested so far"] = "Инвестирани досега",
        ["Expected return"] = "Очаквана доходност",
        ["Horizon"] = "Хоризонт",
        ["years"] = "години",
        ["yr"] = "год.",
        ["Extra /mo"] = "Допълнително / мес.",
        ["Balance owed"] = "Оставаща сума",
        ["Interest rate (% APR)"] = "Лихва (% ГПР)",
        ["Monthly installment"] = "Месечна вноска",
        ["Used only to project your payoff — never changes budgets, savings or balances. Set money aside with 💰, then pay the bank with 🎯."] =
            "Служи само за прогноза на погасяването — не променя бюджети, спестявания или салда. Заделяте пари с 💰, после плащате на банката с 🎯.",
        ["Debts"] = "Дългове",
        ["Add a debt"] = "Добави дълг",
        ["Add debt"] = "Добави дълг",
        ["Archive"] = "Архивирай",
        ["Archive it"] = "Архивирай го",
        ["Archived"] = "Архивирани",
        ["🎉 Paid off!"] = "🎉 Погасен!",
        ["🎉 Goal reached!"] = "🎉 Целта е постигната!",
        ["debt"] = "дълг",
        ["savings"] = "спестявания",
        ["Track a loan or debt: set money aside toward it, then pay the bank. Figures here are projections — they don't move real money."] =
            "Проследявайте заем или дълг: заделяйте пари за него, после плащайте на банката. Числата тук са прогнози — не местят реални пари.",
        // Phase-2 projections + multi-debt planner
        ["Payoff projection"] = "Прогноза за погасяване",
        ["Goal projection"] = "Прогноза за целта",
        ["You're on track for"] = "На път сте към",
        ["Debt-free"] = "Без дългове",
        ["by"] = "до",
        ["reached 🎉"] = "постигната 🎉",
        ["Projections at your current pace — they don't move real money."] =
            "Прогнози при текущото ви темпо — не местят реални пари.",
        ["Set aside"] = "Заделени",
        ["Saved so far"] = "Заделени досега",
        ["Rate"] = "Лихва",
        ["Installment"] = "Вноска",
        ["At your installment"] = "При вашата вноска",
        ["At your saving pace"] = "При вашето темпо на спестяване",
        ["Clear in {0} (~{1}) · interest {2}"] = "Погасяване за {0} (~{1}) · лихва {2}",
        ["You set aside about {0}/period."] = "Заделяте около {0}/период.",
        ["Reach your goal in {0} (~{1})"] = "Ще постигнете целта за {0} (~{1})",
        ["Goal already reached."] = "Целта вече е постигната.",
        ["Projection only — it never changes your budgets, savings or balances."] =
            "Само прогноза — не променя бюджети, спестявания или салда.",
        ["Payoff plan"] = "План за погасяване",
        ["Extra /mo across all debts"] = "Допълнително/мес. за всички дългове",
        ["Avalanche"] = "Лавина",
        ["Snowball"] = "Снежна топка",
        ["Debt-free"] = "Без дългове",
        ["🏔️ Attacking the highest-rate debt first — least interest overall."] =
            "🏔️ Първо най-скъпия дълг — най-малко лихва общо.",
        ["⛄ Attacking the smallest balance first — a quicker first win."] =
            "⛄ Първо най-малкия дълг — по-бърза първа победа.",
        ["Essential spend (rent, groceries, health…)"] = "Основен разход (наем, храна, здраве…)",
        ["Essential budgets are never suggested for redirecting toward a debt."] =
            "Основните бюджети никога не се предлагат за пренасочване към дълг.",
        ["Your {0} budget has about {1} to spare"] = "Бюджетът ви за {0} има около {1} свободни",
        ["Put it toward {0} every period and you'd clear it {1} sooner and save around {2} in interest. Budgets vary, so treat it as a what-if — essential budgets are never counted."] =
            "Ако ги насочвате към {0} всеки период, ще го погасите {1} по-рано и ще спестите около {2} лихва. Бюджетите варират, затова го приемете като хипотеза — основните бюджети никога не се броят.",
        ["Another idea"] = "Друга идея",
        ["Dismiss for this period"] = "Скрий за този период",
        ["clear"] = "погасен",
        ["{0} · total interest {1}"] = "{0} · обща лихва {1}",
        ["That extra clears you {0} sooner and saves {1} in interest."] =
            "Това допълнително ви погасява {0} по-рано и спестява {1} лихва.",
        ["Installment + extra on top"] = "Вноска + допълнително отгоре",
        ["Extra on top /mo"] = "Допълнително отгоре/мес.",
        ["Reset to your saving pace"] = "Върни към темпото на спестяване",
        ["{0} installment + {1} extra = {2}/mo"] = "{0} вноска + {1} допълнително = {2}/мес.",
        [" · your pace ~{0}/period"] = " · вашето темпо ~{0}/период",
        ["{0} sooner · {1} less interest than the installment alone"] =
            "{0} по-рано · {1} по-малко лихва спрямо само вноската",
        ["At this amount it wouldn’t cover the interest."] = "При тази сума не покрива лихвата.",
        ["For planning only — these figures are estimates, not financial advice. Your lender's actual terms (fees, rate changes, payment timing) can differ. For exact numbers, please check with your loan provider."] =
            "Само за планиране — това са приблизителни оценки, не финансов съвет. Реалните условия на кредитора (такси, промени в лихвата, дати на плащане) може да се различават. За точни числа, моля свържете се с вашия кредитор.",
        ["set aside"] = "заделени",
        ["owed"] = "остават",
        // Cross-period trends (#9)
        ["Trends over time"] = "Тенденции във времето",
        ["Savings rate"] = "Норма на спестяване",
        ["Debt owed"] = "Оставащ дълг",
        ["Steady around your {0}-period average of {1}%."] = "Устойчиво около средното за {0} периода от {1}%.",
        ["Up {0} pts vs your {1}-period average of {2}%."] = "С {0} пункта над средното за {1} периода от {2}%.",
        ["Down {0} pts vs your {1}-period average of {2}%."] = "С {0} пункта под средното за {1} периода от {2}%.",
        ["No change over this window."] = "Без промяна в този период.",
        ["Down {0} since {1}."] = "С {0} по-малко от {1}.",
        ["Up {0} since {1}."] = "С {0} повече от {1}.",
        ["First period with spend here."] = "Първи период с разход тук.",
        ["About your {0}-period average of {1}."] = "Около средното за {0} периода от {1}.",
        ["Up {0} vs your {1}-period average of {2}."] = "С {0} над средното за {1} периода от {2}.",
        ["Down {0} vs your {1}-period average of {2}."] = "С {0} под средното за {1} периода от {2}.",
        // P2 — Home quick actions (#11), reminders (#10), milestones (#12)
        ["Repeat last"] = "Повтори последния",
        ["Move to savings"] = "Към спестявания",
        ["Recent"] = "Скорошни",
        ["By day"] = "По ден",
        ["Review"] = "Виж",
        ["You're {0} over your {1} budget."] = "Надхвърли с {0} бюджета за {1}.",
        ["You're {0} over your {1} budget"] = "Надхвърли с {0} бюджета за {1}",
        ["You're {0} from your {1} budget."] = "Остават {0} до бюджета за {1}.",
        ["A non-essential category — easing off here frees up cash for savings or debt."] =
            "Незадължителна категория — спестяването тук освобождава пари за влог или дълг.",
        ["Notifications"] = "Известия",
        ["You're all caught up."] = "Всичко е прегледано.",
        ["Achievements"] = "Постижения",
        ["View all"] = "Виж всички",
        ["{0} of {1} earned"] = "{0} от {1} получени",
        ["You can start next month once this period's end date has passed."] =
            "Можеш да започнеш следващия месец, след като изтече крайната дата на този период.",
        ["Set aside {0} more to hit your {1} savings goal this period."] = "Задели още {0}, за да достигнеш целта от {1} този период.",
        ["No income added this period yet — log it so your plan reflects real money."] =
            "Още няма добавен доход този период — впиши го, за да отразява планът реални пари.",
        ["Add income"] = "Добави доход",
        ["Your remaining budgets are {0} more than you have left — budgets are plans, not commitments, so trim one or top up when you can."] =
            "Оставащите ви бюджети са с {0} повече от парите, които ви остават — бюджетите са планове, не задължения, така че намалете някой или добавете пари, когато можете.",
        ["Money came in this period — move some into savings while it's here."] = "Този период постъпиха средства — задели част, докато са налични.",
        ["Milestones"] = "Постижения",
        ["Saver"] = "Спестовник",
        ["You've set aside {0} in total. Every bit counts."] = "Заделил си общо {0}. Всичко има значение.",
        ["{0}-period saving streak"] = "{0} последователни периода със спестявания",
        ["You've hit your {0} savings target {1} periods running. Keep the chain alive."] = "Достигна целта от {0} през {1} поредни периода. Продължавай!",
        ["Start a saving streak"] = "Започни серия спестявания",
        ["Hit your {0} target 3 periods running to earn this — you're at {1}."] = "Достигни целта от {0} три поредни периода — вече си на {1}.",
        ["First payment down"] = "Първо плащане",
        ["You've made your first payment toward a debt. That's momentum."] = "Направи първото си плащане по дълг. Това е инерция!",
        ["{0} paid off!"] = "{0} е погасен!",
        ["Cleared in full — outstanding work."] = "Погасен изцяло — страхотна работа.",
        ["{0}% of {1} cleared"] = "{0}% от {1} погасени",
        ["{0} of {1} paid off so far."] = "{0} от {1} погасени досега.",
        ["{0} goal reached"] = "Целта {0} е достигната",
        ["You hit your savings goal. Time to celebrate — or set the next one."] = "Достигна целта си. Време за празнуване — или за следваща.",
        // P0 polish (silent no-ops, clamp message, deficit copy)
        ["Spending outran your income"] = "Разходите надвишиха приходите",
        ["{0} of this period's spend isn't backed by fresh cash that came in this period."] = "{0} от разходите този период не са покрити от постъпили този период средства.",
        ["Keep this between 0 and 100% — we'll use {0}%."] = "Стойността трябва да е между 0 и 100% — ще използваме {0}%.",
        ["Amount can't be negative — enter what you spent."] = "Сумата не може да е отрицателна — въведи колко похарчи.",
        ["Enter your username or email and your password."] = "Въведи потребителско име или имейл и парола.",
        ["Fill in a username, email and password to continue."] = "Попълни потребителско име, имейл и парола, за да продължиш.",
        // Progress over time (#7) + planned contribution (#8)
        ["Paid off {0} of {1} ({2}%)"] = "Погасени {0} от {1} ({2}%)",
        ["~{0} ahead of the installment plan"] = "~{0} по-рано от плана с вноски",
        ["Planned contribution /period (optional)"] = "Планиран принос/период (по избор)",
        ["What you plan to put toward this each period, on top of the installment. Used to date your payoff instead of guessing from history. Leave 0 to infer it."] =
            "Колко планирате да внасяте всеки период, над вноската. Използва се за датата на погасяване, вместо да се гадае от историята. Оставете 0, за да се изведе автоматично.",
        ["What you plan to set aside each period. Used to date your goal instead of guessing from history. Leave 0 to infer it."] =
            "Колко планирате да заделяте всеки период. Използва се за датата на целта, вместо да се гадае от историята. Оставете 0, за да се изведе автоматично.",
        ["At your planned contribution"] = "При вашия планиран принос",
        ["You plan to set aside {0}/period."] = "Планирате да заделяте {0}/период.",
        ["Uses the contribution you planned for this bucket, then counts how many periods until you reach the goal."] =
            "Използва планирания от вас принос за този плик и брои колко периода остават до целта.",
        ["Reset to your planned contribution"] = "Върни към планирания принос",
        [" · your plan {0}/period"] = " · вашият план {0}/период",
        ["Make a payment"] = "Направи вноска",
        ["Apply to a goal"] = "Приложи към цел",
        ["Records a real expense paid straight from this bucket (dated today)."] =
            "Записва реален разход, платен директно от тази каса (с днешна дата).",
        ["Previous day"] = "Предишен ден",
        ["Next day"] = "Следващ ден",
        ["All days"] = "Всички дни",

        // Common inline labels / empty states
        ["Amount"] = "Сума",
        ["Note (optional)"] = "Бележка (по избор)",
        ["Nothing on the tab yet — add a deposit."] = "Още нищо тук — добави вноска.",
        ["deposited"] = "внесени",
        ["No funds yet — add where your money lives."] = "Още няма сметки — добави къде стоят парите ти.",
        ["No expenses yet."] = "Още няма разходи.",
        ["Nothing’s perched here yet — add your first expense."] = "Тук още нищо не е кацнало — добави първия си разход.",
        ["No members in this account yet."] = "Още няма членове в този профил.",
        ["No savings yet — start a bucket and watch it grow together."] = "Още няма спестявания — започни каса и я гледай как расте.",
        ["Deposit"] = "Внеси",
        ["Total saved:"] = "Общо спестено:",
        ["Total saved"] = "Общо спестено",
        ["this period:"] = "този период:",
        ["all periods:"] = "всички периоди:",
        ["all periods"] = "всички периоди",

        // Contributions
        ["Categories"] = "Категории",
        ["Add category"] = "Добави категория",
        ["Category"] = "Категория",
        ["Fund"] = "Сметка",
        ["Date"] = "Дата",
        ["Rename"] = "Преименувай",
        ["Edit deposit"] = "Редактирай вноска",
        ["Delete deposit?"] = "Изтриване на вноска?",
        ["New contribution category"] = "Нова категория вноски",
        ["Rename category"] = "Преименувай категория",
        ["Name"] = "Име",
        ["Remove category?"] = "Премахване на категория?",
        ["This removes the category permanently."] = "Това премахва категорията завинаги.",

        // Modal titles
        ["New account"] = "Нов профил",
        ["Rename account"] = "Преименувай профил",
        ["Remove this period?"] = "Премахване на този период?",
        ["Start next month"] = "Започни следващ месец",
        ["Edit expense"] = "Редактирай разход",
        ["Remove this expense?"] = "Премахване на този разход?",
        ["Remove this savings deposit?"] = "Премахване на тази спестовна вноска?",
        ["Edit transfer"] = "Редактирай прехвърляне",
        ["Remove this transfer?"] = "Премахване на това прехвърляне?",
        ["Edit period dates"] = "Редактирай датите на периода",
        ["Edit savings movement"] = "Редактирай спестовно движение",
        ["Remove this outgoing transfer?"] = "Премахване на това изходящо прехвърляне?",
        ["Invite to"] = "Покани в",
        ["Edit savings deposit"] = "Редактирай спестовна вноска",

        // Modal action buttons
        ["Cancel"] = "Отказ",
        ["Save"] = "Запази",
        ["Add"] = "Добави",
        ["Create"] = "Създай",
        ["Delete"] = "Изтрий",
        ["Remove"] = "Премахни",
        ["Close"] = "Затвори",

        // Session 11 features
        ["On behalf of another account (settle later)"] = "От името на друг профил (уреди по-късно)",
        ["Settle onto another account"] = "Прехвърли към друг профил",
        ["Settle"] = "Прехвърли",
        ["Records this amount as an expense on the chosen account (in that fund and category) and reduces this expense by the same amount."] =
            "Записва сумата като разход в избрания профил (в този фонд и категория) и намалява този разход със същата сума.",
        ["Settled onto another account"] = "Прехвърлено към друг профил",
        ["Settled from another account"] = "Прехвърлено от друг профил",
        ["from"] = "от",
        ["Original:"] = "Първоначално:",
        ["Unsettle"] = "Отмени прехвърлянето",
        ["free to allocate"] = "налични за разпределяне",
        ["Over-allocated — allowed, just a heads-up."] = "Преразпределено — позволено е, само за сведение.",
        ["In this fund:"] = "В този фонд:",
        ["⚠ This dips into money earmarked for savings."] = "⚠ Това навлиза в средства, заделени за спестявания.",
        ["your cash that isn't already set aside for savings"] = "парите ти, които още не са заделени за спестявания",
        ["List"] = "Списък",
        ["Calendar"] = "Календар",
        ["Export to Excel"] = "Експорт в Excel",
        ["Manage category"] = "Управление на категорията",
        ["no budget"] = "без бюджет",
        ["No categories yet — add one."] = "Все още няма категории — добави.",
        ["No budgets yet — add a category with a budget (the ➕ above)."] = "Все още няма бюджети — добави категория с бюджет (➕ горе).",
        ["saved"] = "спестено",
        ["spent of"] = "похарчени от",
        ["No budget set for this category."] = "Няма зададен бюджет за тази категория.",
        ["Edit / budget"] = "Редакция / бюджет",
        ["Sub-category"] = "Подкатегория",
        ["No expenses in this category yet."] = "Все още няма разходи в тази категория.",
        ["your money minus savings — spending doesn't lower it"] = "парите ти минус спестяванията — похарченото не ги намалява",
        ["You have no other same-currency account to settle onto."] =
            "Нямаш друг профил в същата валута, към който да прехвърлиш.",
        ["Destination fund"] = "Целеви фонд",
        ["Adjust budgets to this period’s spending"] = "Коригирай бюджетите спрямо разходите за този период",
        ["Each budget moves halfway toward what was actually spent, rounded up to the nearest 10."] =
            "Всеки бюджет се приближава наполовина към реално похарченото, закръглено нагоре до 10.",
        ["Available to budget:"] = "Налично за бюджет:",
        ["the money in the account, minus what's budgeted elsewhere and already saved"] =
            "парите в профила, минус бюджетираното другаде и вече спестеното",
    };
}
