using System.Diagnostics;
using Process = System.Diagnostics.Process;
using System.ComponentModel;
using System.Globalization;

namespace v2rayN.Desktop.Services;

/// <summary>
/// App strings in Russian, Turkish and English, matching the website's wording
/// (web/portal/lib/i18n). XAML binds through <see cref="T"/>; switching the
/// language refreshes every bound text without reopening windows.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();

    public static readonly string[] Languages = ["ru", "tr", "en"];

    private Loc()
    {
        Language = DefaultLanguage();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Changed;

    public string Language { get; private set; }

    public CultureInfo Culture => Language switch
    {
        "ru" => CultureInfo.GetCultureInfo("ru-RU"),
        "tr" => CultureInfo.GetCultureInfo("tr-TR"),
        _ => CultureInfo.GetCultureInfo("en-US")
    };

    public string this[string key] => Get(key);

    public void SetLanguage(string? language)
    {
        var next = Normalize(language);
        if (next == Language) return;
        Language = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        Changed?.Invoke();
    }

    public static string Normalize(string? language)
    {
        var value = (language ?? "").Trim().ToLowerInvariant();
        return Languages.Contains(value) ? value : DefaultLanguage();
    }

    /// <summary>Russian unless the system runs in Turkish or English, like the website.</summary>
    public static string DefaultLanguage()
    {
        var system = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return system switch
        {
            "tr" => "tr",
            "en" => "en",
            _ => "ru"
        };
    }

    public string Get(string key)
    {
        if (Strings.TryGetValue(key, out var values))
        {
            var index = Array.IndexOf(Languages, Language);
            return values[index < 0 ? 0 : index];
        }
        return key;
    }

    public string Format(string key, params (string Name, object Value)[] args)
    {
        var text = Get(key);
        foreach (var (name, value) in args)
        {
            text = text.Replace("{" + name + "}", Convert.ToString(value, Culture));
        }
        return text;
    }

    /// <summary>Counted noun with the right plural form ("3 устройства", "5 дней").</summary>
    public string Count(string noun, long n)
    {
        var form = Language switch
        {
            "ru" => RussianForm(n),
            "en" => n == 1 ? "one" : "many",
            _ => "one"
        };
        return Format($"{noun}.{form}", ("n", n.ToString("N0", Culture)));
    }

    private static string RussianForm(long n)
    {
        var mod10 = Math.Abs(n) % 10;
        var mod100 = Math.Abs(n) % 100;
        if (mod10 == 1 && mod100 != 11) return "one";
        if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return "few";
        return "many";
    }

    public static bool Has(string key) => Strings.ContainsKey(key);

    public static IReadOnlyCollection<string> Keys => Strings.Keys;

    public static string[] ValuesOf(string key) => Strings[key];

    // key → [ru, tr, en]
    private static readonly Dictionary<string, string[]> Strings = new()
    {
        // Shell
        ["nav.home"] = ["Главная", "Ana sayfa", "Home"],
        ["nav.locations"] = ["Локации", "Konumlar", "Locations"],
        ["nav.plan"] = ["Тариф", "Paket", "Plan"],
        ["nav.account"] = ["Аккаунт", "Hesap", "Account"],
        ["nav.settings"] = ["Настройки", "Ayarlar", "Settings"],
        ["window.minimize"] = ["Свернуть", "Küçült", "Minimize"],
        ["window.close"] = ["Свернуть в трей", "Tepsiye küçült", "Hide to tray"],
        ["loading.session"] = ["Восстанавливаем сеанс…", "Oturumunuz açılıyor…", "Restoring your session…"],
        ["update.available"] = ["Обновление {version}", "Güncelleme {version}", "Update {version}"],
        ["update.downloading"] = ["Загрузка {percent}%", "İndiriliyor %{percent}", "Downloading {percent}%"],
        ["update.title"] = ["Доступна версия Colitu {version}", "Colitu {version} hazır", "Colitu {version} is available"],
        ["update.ask"] = ["Обновить сейчас? В старой версии подключение и вход в аккаунт могут работать с ошибками.", "Şimdi güncellemek ister misiniz? Eski sürümde bağlantı ve hesap işlemlerinde sorunlar yaşanabilir.", "Update now? The old version may have problems connecting or signing in."],
        ["update.force"] = ["Эта версия больше не поддерживается. Обновите приложение, чтобы продолжить.", "Bu sürüm artık desteklenmiyor. Devam etmek için uygulamayı güncelleyin.", "This version is no longer supported. Update the app to continue."],
        ["update.now"] = ["Обновить сейчас", "Şimdi güncelle", "Update now"],
        ["update.later"] = ["Позже", "Daha sonra", "Later"],
        ["update.restart"] = ["Colitu закроется, установит обновление и откроется снова.", "Colitu kapanıp güncellemeyi kuracak ve yeniden açılacak.", "Colitu will close, install the update and open again."],
        ["update.installing"] = ["Устанавливаем обновление…", "Güncelleme kuruluyor…", "Installing update…"],
        ["update.failed"] = ["Не удалось установить обновление. Попробуйте позже.", "Güncelleme kurulamadı. Daha sonra tekrar deneyin.", "The update could not be installed. Please try again later."],

        // Connection status
        ["status.protected"] = ["Защищено", "Korunuyor", "Protected"],
        ["status.unprotected"] = ["Не защищено", "Korunmuyor", "Not protected"],
        ["status.connecting"] = ["Подключение…", "Bağlanıyor…", "Connecting…"],
        ["status.reconnecting"] = ["Переподключение…", "Yeniden bağlanıyor…", "Reconnecting…"],
        ["status.disconnecting"] = ["Отключение…", "Bağlantı kesiliyor…", "Disconnecting…"],
        ["status.blocked"] = ["Интернет заблокирован", "İnternet engelli", "Internet blocked"],

        // Home
        ["home.kicker"] = ["COLITU VPN · LINUX", "COLITU VPN · LINUX", "COLITU VPN · LINUX"],
        ["home.title.off"] = ["Соединение не защищено", "Bağlantınız korunmuyor", "Your connection isn’t protected"],
        ["home.title.on"] = ["Вы под защитой", "Korunuyorsunuz", "You’re protected"],
        ["home.title.connecting"] = ["Защищаем соединение", "Bağlantınız güvenceye alınıyor", "Securing your connection"],
        ["home.title.noplan"] = ["Выберите тариф, чтобы начать", "Başlamak için bir paket seçin", "Choose a plan to get started"],
        ["home.sub.off"] = ["Нажмите кнопку — трафик будет зашифрован, а IP-адрес скрыт.", "Düğmeye basın; trafiğiniz şifrelenir, IP adresiniz gizlenir.", "Press the button to encrypt your traffic and hide your IP address."],
        ["home.sub.on"] = ["Трафик зашифрован и идёт через {server}.", "Trafiğiniz şifreli ve {server} üzerinden geçiyor.", "Your traffic is encrypted and routed through {server}."],
        ["home.sub.connecting"] = ["Готовим зашифрованный туннель…", "Şifreli tünel hazırlanıyor…", "Setting up the encrypted tunnel…"],
        ["home.sub.noplan"] = ["Все серверы, безлимитный трафик и защита от утечек — в одном тарифе.", "Tüm sunucular, sınırsız trafik ve sızıntı koruması tek pakette.", "Every server, unlimited traffic and leak protection in one plan."],
        ["home.title.blocked"] = ["Интернет заблокирован", "İnternet engellendi", "Internet blocked"],
        ["home.sub.blocked"] = ["VPN прервался, и kill switch закрыл интернет, чтобы ничего не утекло. Переподключаемся автоматически — или отключите защиту.", "VPN koptu; kill switch hiçbir şey sızmasın diye interneti kapattı. Otomatik olarak yeniden bağlanıyoruz; isterseniz korumayı kapatın.", "The VPN dropped, so the kill switch closed the internet to keep anything from leaking. We’re reconnecting automatically, or you can turn protection off."],
        ["home.unblock"] = ["Отключить защиту и открыть интернет", "Korumayı kapat ve interneti aç", "Turn protection off and restore internet"],
        ["home.connect"] = ["Подключить", "Bağlan", "Connect"],
        ["home.disconnect"] = ["Отключить", "Bağlantıyı kes", "Disconnect"],
        ["home.cancel"] = ["Отменить", "İptal", "Cancel"],
        ["home.tap"] = ["Нажмите, чтобы подключиться", "Bağlanmak için tıklayın", "Click to connect"],
        ["home.tapOff"] = ["Нажмите, чтобы отключить", "Kesmek için tıklayın", "Click to disconnect"],
        ["home.tapCancel"] = ["Нажмите, чтобы отменить", "İptal etmek için tıklayın", "Click to cancel"],
        ["home.protocol"] = ["Протокол", "Protokol", "Protocol"],
        // Colitu names of the transports; the technical names stay out of the UI.
        ["transport.hysteria2"] = ["Быстрый", "Hızlı", "Fast"],
        ["transport.vless-reality"] = ["Скрытный", "Gizli", "Stealth"],
        ["transport.vless-xhttp"] = ["Устойчивый", "Dayanıklı", "Resilient"],
        ["transport.trojan"] = ["Классический", "Klasik", "Classic"],
        ["transport.shadowsocks"] = ["Лёгкий", "Hafif", "Light"],
        ["transport.other"] = ["Резервный", "Yedek", "Backup"],
        ["home.changeServer"] = ["Сменить сервер", "Sunucuyu değiştir", "Change server"],
        ["home.session"] = ["Время подключения", "Bağlantı süresi", "Connection time"],
        ["home.location"] = ["ЛОКАЦИЯ", "KONUM", "LOCATION"],
        ["home.change"] = ["Изменить", "Değiştir", "Change"],
        ["home.plan"] = ["ТАРИФ", "PAKET", "PLAN"],
        ["home.quick"] = ["БЫСТРЫЕ НАСТРОЙКИ", "HIZLI AYARLAR", "QUICK SETTINGS"],
        ["home.traffic"] = ["Трафик за период", "Bu dönemki trafik", "Traffic this period"],
        ["home.trafficOf"] = ["{used} из {limit}", "{used} / {limit}", "{used} of {limit}"],
        ["home.unlimited"] = ["Безлимитный трафик", "Sınırsız trafik", "Unlimited traffic"],
        ["home.devices"] = ["Устройства", "Cihazlar", "Devices"],
        ["home.offline"] = ["Нет связи с сервером Colitu. Повторяем попытку…", "Colitu sunucusuna ulaşılamıyor. Yeniden deneniyor…", "Can’t reach Colitu right now. Retrying…"],

        ["server.auto"] = ["Лучший сервер", "En iyi sunucu", "Best server"],
        ["server.autoHint"] = ["Colitu сам выберет самый свободный и стабильный", "Colitu en boş ve kararlı sunucuyu kendisi seçer", "Colitu picks the least busy, most stable one"],
        ["server.none"] = ["Серверы появятся здесь", "Sunucular burada görünecek", "Servers will appear here"],
        ["server.load.low"] = ["Низкая нагрузка", "Düşük yük", "Low load"],
        ["server.load.medium"] = ["Средняя нагрузка", "Orta yük", "Medium load"],
        ["server.load.high"] = ["Высокая нагрузка", "Yüksek yük", "High load"],
        ["server.offline"] = ["Недоступен", "Çevrimdışı", "Offline"],
        ["server.connected"] = ["Подключено", "Bağlı", "Connected"],
        ["server.selected"] = ["Выбрано", "Seçili", "Selected"],

        // Locations
        ["locations.kicker"] = ["ЛОКАЦИИ", "KONUMLAR", "LOCATIONS"],
        ["locations.title"] = ["Выберите локацию", "Bir konum seçin", "Choose a location"],
        ["locations.sub"] = ["Серверов онлайн: {n}. При смене локации подключение переключится автоматически.", "Çevrimiçi sunucu: {n}. Konumu değiştirdiğinizde bağlantı otomatik olarak geçer.", "{n} servers online. Switching location moves your connection automatically."],
        ["locations.search"] = ["Поиск страны или города", "Ülke veya şehir ara", "Search country or city"],
        ["locations.empty"] = ["Ничего не найдено", "Sonuç bulunamadı", "Nothing found"],
        ["locations.switching"] = ["Переключаемся на {server}…", "{server} konumuna geçiliyor…", "Switching to {server}…"],
        ["locations.switched"] = ["Подключено: {server}", "Bağlandı: {server}", "Connected to {server}"],

        // Plan status
        ["plan.none"] = ["Нет активного тарифа", "Aktif paket yok", "No active plan"],
        ["plan.noneHint"] = ["Выберите тариф, чтобы подключиться.", "Bağlanmak için bir paket seçin.", "Choose a plan to connect."],
        ["plan.status.active"] = ["АКТИВЕН", "AKTİF", "ACTIVE"],
        ["plan.status.trialing"] = ["ПРОБНЫЙ", "DENEME", "TRIAL"],
        ["plan.status.expired"] = ["ИСТЁК", "SÜRESİ DOLDU", "EXPIRED"],
        ["plan.status.inactive"] = ["НЕ АКТИВЕН", "PASİF", "INACTIVE"],
        ["plan.until"] = ["До {date}", "{date} tarihine kadar", "Until {date}"],
        ["plan.left"] = ["осталось {left}", "{left} kaldı", "{left} left"],
        ["plan.choose"] = ["Выбрать тариф", "Paket seç", "Choose a plan"],
        ["plan.extend"] = ["Продлить", "Süreyi uzat", "Extend"],
        ["plan.freeName"] = ["Бесплатный тариф", "Ücretsiz plan", "Free plan"],
        ["plan.freeHint"] = ["10 ГБ каждый месяц", "Her ay 10 GB", "10 GB every month"],
        ["plan.upgrade"] = ["Перейти на безлимит", "Sınırsız trafiğe geç", "Go unlimited"],
        ["plan.status.quota"] = ["ЛИМИТ ИСЧЕРПАН", "KOTA DOLDU", "LIMIT REACHED"],
        ["plan.trialName"] = ["Пробный период", "Deneme süresi", "Free trial"],
        ["day.one"] = ["{n} день", "{n} gün", "{n} day"],
        ["day.few"] = ["{n} дня", "{n} gün", "{n} days"],
        ["day.many"] = ["{n} дней", "{n} gün", "{n} days"],
        ["hour.one"] = ["{n} час", "{n} saat", "{n} hour"],
        ["hour.few"] = ["{n} часа", "{n} saat", "{n} hours"],
        ["hour.many"] = ["{n} часов", "{n} saat", "{n} hours"],
        ["device.one"] = ["{n} устройство", "{n} cihaz", "{n} device"],
        ["device.few"] = ["{n} устройства", "{n} cihaz", "{n} devices"],
        ["device.many"] = ["{n} устройств", "{n} cihaz", "{n} devices"],

        // Pricing (same wording as colitu.com/pricing)
        ["pricing.kicker"] = ["ТАРИФЫ", "PAKETLER", "PLANS"],
        ["pricing.title"] = ["Один тариф. Все серверы.", "Tek paket. Tüm sunucular.", "One plan. Every server."],
        ["pricing.sub"] = ["Тариф покупается и продлевается в личном кабинете на colitu.com.", "Paket satın alma ve süre uzatma colitu.com hesabınızdan yapılır.", "Plans are bought and renewed in your account on colitu.com."],
        ["plan.manageTitle"] = ["Подписка управляется на colitu.com", "Abonelik colitu.com üzerinden yönetilir", "Your subscription is managed on colitu.com"],
        ["plan.manageBody"] = ["Тариф, продление и устройства — в личном кабинете. Войдите с той же почтой, что и в приложении.", "Paket, süre uzatma ve cihazlar hesabınızdan yönetilir. Uygulamadaki e-posta adresinizle giriş yapın.", "Plans, renewals and devices are handled in your account. Sign in with the same e-mail as in the app."],
        ["plan.manageButton"] = ["Открыть app.colitu.com", "app.colitu.com’u aç", "Open app.colitu.com"],
        ["plan.refresh"] = ["Обновить статус", "Durumu yenile", "Refresh status"],
        ["plan.refreshHint"] = ["После изменений в кабинете вернитесь сюда и обновите статус.", "Hesabınızda değişiklik yaptıktan sonra buraya dönüp durumu yenileyin.", "After changing something in your account, come back and refresh."],
        ["plan.refreshed"] = ["Статус обновлён", "Durum güncellendi", "Status updated"],
        ["pricing.current"] = ["Текущий тариф", "Mevcut paket", "Current plan"],
        ["pricing.months"] = ["{n} МЕС.", "{n} AY", "{n} MONTHS"],
        ["pricing.oneMonth"] = ["1 МЕС.", "1 AY", "1 MONTH"],
        ["pricing.twoYears"] = ["2 ГОДА", "2 YIL", "2 YEARS"],
        ["pricing.off"] = ["Скидка {n}%", "%{n} İNDİRİM", "{n}% off"],
        ["pricing.best"] = ["САМОЕ ВЫГОДНОЕ ПРЕДЛОЖЕНИЕ", "EN AVANTAJLI · EN ÇOK TASARRUF", "BEST VALUE · BIGGEST SAVINGS"],
        ["pricing.perDevice"] = ["в месяц за устройство", "aylık, cihaz başına", "per device a month"],
        ["pricing.saving"] = ["Экономия {amount} на устройство", "Cihaz başına {amount} tasarruf", "Save {amount} per device"],
        ["pricing.devices"] = ["Количество устройств", "Cihaz sayısı", "Number of devices"],
        ["pricing.method"] = ["Способ оплаты", "Ödeme yöntemi", "Payment method"],
        ["pricing.noFee"] = ["Без комиссии", "Komisyonsuz", "No fee"],
        ["pricing.fee"] = ["Комиссия {n}%", "Komisyon %{n}", "{n}% fee"],
        ["pricing.summary"] = ["ИТОГ", "ÖZET", "SUMMARY"],
        ["pricing.regular"] = ["Обычная цена", "Normal fiyat", "Regular price"],
        ["pricing.discount"] = ["Скидка ({n}%)", "İndirim (%{n})", "Discount ({n}%)"],
        ["pricing.commission"] = ["Комиссия", "Komisyon", "Payment fee"],
        ["pricing.total"] = ["ИТОГО", "TOPLAM", "TOTAL"],
        ["pricing.pay"] = ["Перейти к безопасной оплате", "Güvenli ödemeye geç", "Continue to secure payment"],
        ["pricing.secure"] = ["Оплата проходит на защищённой странице платёжного сервиса. Доступ откроется сразу после оплаты.", "Ödeme, ödeme sağlayıcısının güvenli sayfasında yapılır. Erişiminiz ödemeden hemen sonra açılır.", "You pay on the payment provider’s secure page. Access opens as soon as the payment clears."],
        ["pricing.loadFailed"] = ["Не удалось загрузить тарифы.", "Paketler yüklenemedi.", "Plans could not be loaded."],
        ["pricing.retry"] = ["Повторить", "Tekrar dene", "Try again"],
        ["pay.checking"] = ["Проверяем оплату…", "Ödeme kontrol ediliyor...", "Checking payment…"],
        ["pay.checkingHint"] = ["Завершите оплату в браузере. Эта страница обновится автоматически.", "Ödemeyi tarayıcıda tamamlayın. Bu sayfa otomatik olarak güncellenir.", "Finish paying in your browser. This page updates by itself."],
        ["pay.reopen"] = ["Открыть страницу оплаты", "Ödeme sayfasını aç", "Open payment page"],
        ["pay.cancel"] = ["Отменить", "Vazgeç", "Cancel"],
        ["pay.success"] = ["Оплата прошла", "Ödeme alındı", "Payment received"],
        ["pay.successHint"] = ["Тариф активен. Можно подключаться.", "Paketiniz aktif. Hemen bağlanabilirsiniz.", "Your plan is active. You can connect now."],
        ["pay.failed"] = ["Оплата не прошла", "Ödeme tamamlanmadı", "Payment didn’t go through"],
        ["pay.failedHint"] = ["Деньги не списаны. Попробуйте ещё раз или выберите другой способ.", "Ücret alınmadı. Tekrar deneyin veya başka bir yöntem seçin.", "You were not charged. Try again or pick another method."],
        ["pay.back"] = ["Вернуться к тарифам", "Paketlere dön", "Back to plans"],
        ["pay.connect"] = ["Подключиться", "Bağlan", "Connect now"],

        // Account
        ["account.kicker"] = ["АККАУНТ", "HESAP", "ACCOUNT"],
        ["account.title"] = ["Ваш аккаунт", "Hesabınız", "Your account"],
        ["account.email"] = ["Электронная почта", "E-posta", "Email"],
        ["account.plan"] = ["Тариф", "Paket", "Plan"],
        ["account.validUntil"] = ["Действует до", "Geçerlilik", "Valid until"],
        ["account.devices"] = ["УСТРОЙСТВА", "CİHAZLAR", "DEVICES"],
        ["account.devicesTitle"] = ["Подключено {used} из {limit}", "{limit} cihazdan {used} tanesi bağlı", "{used} of {limit} in use"],
        ["account.thisDevice"] = ["ЭТО УСТРОЙСТВО", "BU CİHAZ", "THIS DEVICE"],
        ["account.lastSeen"] = ["Активность: {date}", "Son etkinlik: {date}", "Last active {date}"],
        ["account.remove"] = ["Отключить устройство", "Cihazı kaldır", "Remove device"],
        ["account.removeConfirm"] = ["Отключить «{name}»? На нём потребуется войти снова.", "“{name}” kaldırılsın mı? Bu cihazda yeniden giriş yapmak gerekecek.", "Remove “{name}”? It will need to sign in again."],
        ["account.manage"] = ["Управлять на colitu.com", "colitu.com’da yönet", "Manage on colitu.com"],
        ["account.signOut"] = ["Выйти", "Çıkış yap", "Sign out"],
        ["account.help"] = ["Нужна помощь?", "Yardım mı lazım?", "Need help?"],
        ["account.helpHint"] = ["Напишите нам — отвечаем быстро и по-человечески.", "Bize yazın; hızlı ve insan gibi yanıt veririz.", "Write to us. We answer quickly, and a real person reads it."],
        ["account.support"] = ["Центр поддержки", "Destek merkezi", "Support center"],
        ["account.mail"] = ["Написать на почту", "E-posta gönder", "Email support"],

        // Settings
        ["settings.kicker"] = ["НАСТРОЙКИ", "AYARLAR", "SETTINGS"],
        ["settings.title"] = ["Настройки", "Ayarlar", "Settings"],
        ["settings.language"] = ["Язык", "Dil", "Language"],
        ["settings.connection"] = ["Подключение", "Bağlantı", "Connection"],
        ["settings.mode"] = ["Режим", "Mod", "Mode"],
        ["settings.mode.proxy"] = ["Прокси", "Proxy", "Proxy"],
        ["settings.mode.tun"] = ["Весь трафик (TUN)", "Tüm trafik (TUN)", "All traffic (TUN)"],
        ["settings.mode.proxyHint"] = ["Через VPN идут только браузеры и приложения, которые используют системный прокси.", "VPN’den yalnızca sistem proxy’sini kullanan tarayıcılar ve uygulamalar geçer.", "Only browsers and apps that use the system proxy go through the VPN."],
        ["proxy.warn"] = ["В режиме прокси не защищены: DNS-запросы, UDP/WebRTC, IPv6 и приложения, которые игнорируют системный прокси. Чтобы защитить всё, включите режим TUN.", "Proxy modunda şunlar korunmaz: DNS sorguları, UDP/WebRTC, IPv6 ve sistem proxy’sini yok sayan uygulamalar. Her şeyi korumak için TUN modunu açın.", "Proxy mode does not protect DNS lookups, UDP/WebRTC, IPv6 or apps that ignore the system proxy. Switch to TUN mode to protect everything."],
        ["proxy.warnShort"] = ["Режим прокси: DNS, UDP/WebRTC, IPv6 и приложения без системного прокси не защищены.", "Proxy modu: DNS, UDP/WebRTC, IPv6 ve sistem proxy’sini yok sayan uygulamalar korunmuyor.", "Proxy mode: DNS, UDP/WebRTC, IPv6 and apps that ignore the system proxy are not protected."],
        ["settings.split"] = ["Раздельное туннелирование", "Bölünmüş tünel", "Split tunneling"],
        ["settings.split.hint"] = ["Выберите приложения и сайты, которые идут мимо VPN, — или единственные, которые идут через него.", "VPN’i atlayacak uygulama ve siteleri ya da VPN’i kullanacak tek uygulama ve siteleri seçin.", "Choose apps and sites that skip the VPN, or the only ones that use it."],
        ["settings.split.off"] = ["Выкл.", "Kapalı", "Off"],
        ["settings.split.exclude"] = ["Выбранные идут мимо VPN", "Seçilenler VPN’i atlar", "Selected apps and sites bypass the VPN"],
        ["settings.split.include"] = ["Только выбранные через VPN", "Yalnızca seçilenler VPN’i kullanır", "Only selected apps and sites use the VPN"],
        ["settings.split.apps"] = ["Приложения", "Uygulamalar", "Apps"],
        ["settings.split.appsHint"] = ["Имя программы (firefox) или полный путь (/usr/lib/firefox/firefox), по одному в строке. Только в режиме TUN.", "Program adı (firefox) veya tam yolu (/usr/lib/firefox/firefox), her satıra bir tane. Yalnızca TUN modunda.", "Program name (firefox) or full path (/usr/lib/firefox/firefox), one per line. TUN mode only."],
        ["settings.split.domains"] = ["Сайты (домены)", "Siteler (alan adları)", "Sites (domains)"],
        ["settings.split.domainsHint"] = ["example.com включает и его поддомены. По одному в строке.", "example.com alt alan adlarını da kapsar. Her satıra bir tane.", "example.com also covers its subdomains. One per line."],
        ["settings.split.ips"] = ["IP-адреса и диапазоны", "IP adresleri ve aralıklar", "IP addresses and ranges"],
        ["settings.split.ipsHint"] = ["203.0.113.7 или 10.0.0.0/8, IPv4 и IPv6. По одному в строке.", "203.0.113.7 veya 10.0.0.0/8, IPv4 ve IPv6. Her satıra bir tane.", "203.0.113.7 or 10.0.0.0/8, IPv4 and IPv6. One per line."],
        ["settings.split.proxyNote"] = ["В режиме прокси разделяются только сайты (домены и IP). Для приложений нужен режим TUN.", "Proxy modunda yalnızca siteler (alan adları ve IP’ler) ayrılır. Uygulamalar için TUN modu gerekir.", "In proxy mode only sites (domains and IPs) are split. Apps need TUN mode."],
        ["settings.split.includeNote"] = ["Всё, чего нет в списках, идёт мимо VPN и не защищено.", "Listelerde olmayan her şey VPN dışından, korumasız gider.", "Everything not on the lists goes outside the VPN, unprotected."],
        ["settings.split.killSwitchNote"] = ["Kill switch пропускает трафик мимо VPN, пока VPN работает. Если VPN отключится, доступными останутся только IP-адреса из списка.", "Kill switch, VPN açıkken VPN’i atlayan trafiğe izin verir. VPN koparsa yalnızca listedeki IP adresleri erişilebilir kalır.", "The kill switch lets the bypassing traffic through while the VPN is on. If the VPN drops, only the listed IP addresses stay reachable."],
        ["settings.split.save"] = ["Сохранить", "Kaydet", "Save"],
        ["settings.split.invalid"] = ["Не сохранено: эти записи некорректны: {items}", "Kaydedilmedi; şu girişler geçersiz: {items}", "Not saved. These entries aren’t valid: {items}"],
        ["settings.split.empty"] = ["Добавьте хотя бы одно приложение или сайт: с пустыми списками раздельное туннелирование не работает.", "En az bir uygulama veya site ekleyin; listeler boşken bölünmüş tünel çalışmaz.", "Add at least one app or site: with empty lists split tunneling does nothing."],
        ["home.split.exclude"] = ["Раздельное туннелирование: мимо VPN — {n} (приложения/сайты)", "Bölünmüş tünel açık: {n} uygulama/site VPN dışında", "Split tunneling on: {n} apps/sites outside the VPN"],
        ["home.split.include"] = ["Раздельное туннелирование: через VPN — только {n} (приложения/сайты)", "Bölünmüş tünel açık: VPN’i yalnızca {n} uygulama/site kullanıyor", "Split tunneling on: only {n} apps/sites use the VPN"],
        ["tunOffer.kicker"] = ["РЕКОМЕНДУЕМ", "ÖNERİLEN", "RECOMMENDED"],
        ["tunOffer.title"] = ["Защитите весь трафик", "Tüm trafiğinizi koruyun", "Protect all of your traffic"],
        ["tunOffer.body"] = ["В режиме TUN через VPN идёт всё, что делает этот компьютер: DNS, игры, мессенджеры, звонки по UDP/WebRTC и приложения, которые игнорируют прокси. Kill switch на Linux тоже работает только в режиме TUN.", "TUN modunda bu bilgisayardaki her şey VPN’den geçer: DNS, oyunlar, mesajlaşma uygulamaları, UDP/WebRTC aramaları ve proxy ayarını yok sayan uygulamalar. Linux’ta kill switch de yalnızca TUN modunda çalışır.", "In TUN mode everything on this computer goes through the VPN: DNS, games, messengers, UDP/WebRTC calls and apps that ignore the proxy setting. On Linux the kill switch also works only in TUN mode."],
        ["tunOffer.sudo"] = ["Для этого Colitu при каждом запуске попросит пароль администратора (sudo), чтобы создать защищённый адаптер. Пароль хранится только в памяти.", "Bunun için Colitu, güvenli bağdaştırıcıyı kurmak üzere her açılışta yönetici (sudo) parolanızı ister. Parola yalnızca bellekte tutulur.", "Colitu then asks for your administrator (sudo) password each time it starts, to set up the secure adapter. The password stays in memory only."],
        ["tunOffer.accept"] = ["Использовать режим TUN", "TUN modunu kullan", "Use TUN mode"],
        ["tunOffer.keep"] = ["Оставить режим прокси", "Proxy modunda kal", "Keep proxy mode"],
        ["tunOffer.note"] = ["Режим можно сменить в любой момент в настройках.", "Modu istediğiniz zaman Ayarlar’dan değiştirebilirsiniz.", "You can change the mode any time in Settings."],
        ["tunOffer.done"] = ["Режим TUN включён. Пароль sudo понадобится при подключении.", "TUN modu açıldı. Bağlanırken sudo parolanız istenecek.", "TUN mode is on. Colitu asks for the sudo password when you connect."],
        ["settings.mode.tunHint"] = ["Весь трафик Linux идёт через защищённый адаптер, включая игры и мессенджеры. Нужен пароль администратора (sudo).", "Oyunlar ve mesajlaşma dahil tüm Linux trafiği güvenli bağdaştırıcıdan geçer. Yönetici (sudo) parolası gerekir.", "All Linux traffic, games and messengers included, goes through the secure adapter. Needs the administrator (sudo) password."],
        ["settings.killSwitch"] = ["Kill switch", "Kill switch", "Kill switch"],
        ["settings.killSwitchHint"] = ["Если VPN неожиданно отключится, интернет блокируется, пока защита не вернётся. На Linux работает в режиме TUN.", "VPN beklenmedik şekilde koparsa koruma geri gelene kadar internet engellenir. Linux’ta TUN modunda çalışır.", "If the VPN drops unexpectedly, the internet stays blocked until protection is back. On Linux it works in TUN mode."],
        ["settings.dns"] = ["Защита от утечек DNS", "DNS sızıntı koruması", "DNS leak protection"],
        ["settings.dnsHint"] = ["В режиме TUN все DNS-запросы идут через VPN. В режиме прокси защищены запросы приложений, использующих системный прокси.", "TUN modunda tüm DNS sorguları VPN üzerinden gider. Proxy modunda yalnızca sistem proxy'sini kullanan uygulamaların sorguları korunur.", "In TUN mode every DNS lookup goes through the VPN. In proxy mode only apps that use the system proxy are covered."],
        ["settings.autoConnect"] = ["Автоподключение", "Otomatik bağlan", "Auto-connect"],
        ["settings.autoConnectHint"] = ["Подключаться сразу после запуска приложения.", "Uygulama açılır açılmaz bağlan.", "Connect as soon as the app starts."],
        ["settings.adBlock"] = ["Блокировка рекламы", "Reklam engelleme", "Ad blocking"],
        ["settings.adBlockHint"] = ["Реклама и трекеры блокируются на DNS-серверах Colitu. Журнал запросов не ведётся. Если какой-то сайт сломается, выключите.", "Reklam ve izleyiciler Colitu DNS sunucularında engellenir. Sorgu kaydı tutulmaz. Bir site bozulursa kapatın.", "Ads and trackers are blocked on Colitu’s DNS servers. No query log is kept. If a site breaks, turn it off."],
        ["settings.app"] = ["Приложение", "Uygulama", "App"],
        ["settings.startup"] = ["Запускать при входе в систему", "Oturum açılınca başlat", "Start at sign-in"],
        ["settings.startupHint"] = ["Colitu откроется в трее при входе в систему.", "Colitu, oturum açıldığında tepside başlar.", "Colitu starts in the tray when you sign in."],
        ["settings.tray"] = ["Сворачивать в трей при закрытии", "Kapatınca tepsiye küçült", "Keep running in the tray"],
        ["settings.trayHint"] = ["Защита остаётся включённой, пока окно закрыто.", "Pencere kapalıyken koruma açık kalır.", "Protection stays on while the window is closed."],
        ["settings.about"] = ["О приложении", "Hakkında", "About"],
        ["settings.version"] = ["Версия {version}", "Sürüm {version}", "Version {version}"],
        ["settings.checkUpdates"] = ["Проверить обновления", "Güncellemeleri denetle", "Check for updates"],
        ["settings.upToDate"] = ["У вас последняя версия.", "En güncel sürümü kullanıyorsunuz.", "You’re on the latest version."],
        ["settings.checking"] = ["Проверяем…", "Denetleniyor…", "Checking…"],
        ["settings.privacy"] = ["Конфиденциальность", "Gizlilik", "Privacy"],
        ["settings.terms"] = ["Условия", "Koşullar", "Terms"],
        ["settings.website"] = ["colitu.com", "colitu.com", "colitu.com"],
        ["settings.logs"] = ["Открыть журналы", "Günlükleri aç", "Open logs"],
        ["settings.saved"] = ["Сохранено", "Kaydedildi", "Saved"],
        ["settings.reconnectHint"] = ["Изменения применятся при следующем подключении.", "Değişiklik bir sonraki bağlantıda uygulanır.", "Changes apply the next time you connect."],
        ["about.openSource"] = ["Открытый исходный код", "Açık kaynak", "Open source"],
        ["about.openSourceHint"] = ["Colitu для Linux распространяется под лицензией GPL-3.0. Код открыт на GitHub: github.com/colitu/linux", "Colitu Linux uygulaması GPL-3.0 lisanslıdır. Kaynak kodu GitHub'da: github.com/colitu/linux", "Colitu for Linux is licensed under GPL-3.0. The source code is on GitHub: github.com/colitu/linux"],
        ["about.viewSource"] = ["Открыть на GitHub", "GitHub'da aç", "View on GitHub"],
        ["about.credits"] = ["Основано на v2rayN (GPL-3.0), Xray-core (MPL-2.0) и sing-box (GPL-3.0).", "v2rayN (GPL-3.0), Xray-core (MPL-2.0) ve sing-box (GPL-3.0) üzerine kuruludur.", "Built on v2rayN (GPL-3.0), Xray-core (MPL-2.0) and sing-box (GPL-3.0)."],
        ["brand.credit"] = ["© {brand}", "© {brand}", "© {brand}"],

        // Auth
        ["auth.kicker"] = ["БЕЗОПАСНО · ПРИВАТНО · БЫСТРО", "GÜVENLİ · ÖZEL · HIZLI", "SECURE · PRIVATE · FAST"],
        ["auth.heroTitle"] = ["Свободный интернет в одно касание.", "Özgür internet, tek dokunuşla.", "The open internet, one tap away."],
        ["auth.heroSub"] = ["Серверы в разных странах, шифрование трафика и никаких журналов.", "Farklı ülkelerde sunucular, şifreli trafik ve kayıt tutmama.", "Servers in many countries, encrypted traffic and no activity logs."],
        ["auth.login"] = ["Вход", "Giriş", "Sign in"],
        ["auth.register"] = ["Регистрация", "Kayıt ol", "Sign up"],
        ["auth.loginTitle"] = ["С возвращением", "Tekrar hoş geldiniz", "Welcome back"],
        ["auth.loginSub"] = ["Войдите в аккаунт Colitu — тот же, что на colitu.com.", "Colitu hesabınızla giriş yapın; colitu.com ile aynı hesap.", "Sign in with your Colitu account, the same one you use on colitu.com."],
        ["auth.registerTitle"] = ["Создать аккаунт", "Hesap oluştur", "Create account"],
        ["reset.title"] = ["Восстановление пароля", "Şifrenizi sıfırlayın", "Reset your password"],
        ["reset.sub"] = ["Введите почту аккаунта — мы отправим на неё 6-значный код.", "Hesabınızın e-posta adresini girin; 6 haneli bir kod gönderelim.", "Enter your account email and we’ll send you a 6-digit code."],
        ["reset.codeSub"] = ["Введите код из письма и придумайте новый пароль.", "E-postadaki kodu girin ve yeni bir şifre belirleyin.", "Enter the code from the email and choose a new password."],
        ["reset.send"] = ["Отправить код", "Kod gönder", "Send code"],
        ["reset.sent"] = ["Код отправлен на {email}. Проверьте и папку «Спам».", "{email} adresine kod gönderdik. Gereksiz (spam) klasörünü de kontrol edin.", "We sent a code to {email}. Check your spam folder too."],
        ["reset.newPassword"] = ["Новый пароль", "Yeni şifre", "New password"],
        ["reset.submit"] = ["Сменить пароль и войти", "Şifreyi değiştir ve giriş yap", "Change password and sign in"],
        ["reset.changeEmail"] = ["Изменить почту", "E-postayı değiştir", "Change email"],
        ["reset.back"] = ["Вернуться ко входу", "Girişe dön", "Back to sign in"],
        ["reset.note"] = ["После смены пароля остальные устройства выйдут из аккаунта.", "Şifre değişince diğer cihazlardaki oturumlar kapanır.", "Changing the password signs you out on your other devices."],
        ["reset.done"] = ["Пароль изменён. Вы вошли в аккаунт.", "Şifreniz değiştirildi, giriş yaptınız.", "Your password was changed and you’re signed in."],
        ["auth.registerSub"] = ["Зарегистрируйтесь по почте и пользуйтесь Colitu бесплатно: 10 ГБ каждый месяц.", "E-postanızla kaydolun, Colitu’yu ücretsiz kullanın: her ay 10 GB.", "Sign up with your email and use Colitu for free: 10 GB every month."],
        ["auth.email"] = ["Электронная почта", "E-posta", "Email"],
        ["auth.emailHint"] = ["you@example.com", "ornek@eposta.com", "you@example.com"],
        ["auth.password"] = ["Пароль", "Şifre", "Password"],
        ["auth.passwordHint"] = ["Не менее 10 символов", "En az 10 karakter", "At least 10 characters"],
        ["auth.passwordRepeat"] = ["Повторите пароль", "Şifreyi tekrarla", "Repeat password"],
        ["auth.show"] = ["Показать пароль", "Şifreyi göster", "Show password"],
        ["auth.terms"] = ["Я принимаю условия и политику конфиденциальности", "Koşulları ve gizlilik politikasını kabul ediyorum", "I accept the terms and privacy policy"],
        ["auth.submitLogin"] = ["Войти", "Giriş yap", "Sign in"],
        ["auth.submitRegister"] = ["Создать аккаунт", "Hesap oluştur", "Create account"],
        ["auth.forgot"] = ["Забыли пароль?", "Şifremi unuttum", "Forgot password?"],
        ["auth.remember"] = ["Вы останетесь в аккаунте на этом компьютере.", "Bu bilgisayarda oturumunuz açık kalır.", "You’ll stay signed in on this computer."],
        ["auth.err.email"] = ["Введите корректный адрес почты.", "Geçerli bir e-posta adresi girin.", "Enter a valid email address."],
        ["auth.err.password"] = ["Пароль должен быть не короче 10 символов.", "Şifre en az 10 karakter olmalı.", "The password must be at least 10 characters."],
        ["auth.err.mismatch"] = ["Пароли не совпадают.", "Şifreler eşleşmiyor.", "Passwords don’t match."],
        ["auth.err.terms"] = ["Примите условия, чтобы продолжить.", "Devam etmek için koşulları kabul edin.", "Accept the terms to continue."],
        ["auth.expired"] = ["Сеанс завершён. Войдите снова.", "Oturumunuz sona erdi. Lütfen tekrar giriş yapın.", "Your session ended. Please sign in again."],

        // Errors
        ["err.credentials"] = ["Неверная почта или пароль.", "E-posta veya şifre hatalı.", "Email or password is incorrect."],
        ["err.registration"] = ["Эту почту нельзя зарегистрировать: возможно, аккаунт уже существует.", "Bu e-posta ile kayıt yapılamıyor; hesap zaten olabilir.", "This email can’t be registered. It may already have an account."],
        ["err.rateLimited"] = ["Слишком много попыток. Подождите минуту.", "Çok fazla deneme. Lütfen bir dakika bekleyin.", "Too many attempts. Please wait a minute."],
        ["err.deviceLimit"] = ["Достигнут лимит устройств вашего тарифа. Отключите старое устройство на colitu.com/devices или добавьте место в тариф.", "Paketinizin cihaz sınırına ulaşıldı. colitu.com/devices adresinden eski bir cihazı kaldırın veya paketinize cihaz ekleyin.", "Your plan’s device limit is reached. Remove an old device at colitu.com/devices or add a seat to your plan."],
        ["err.region"] = ["Регистрация и вход из вашего региона сейчас недоступны. Если вы используете другой VPN или прокси, отключите его и попробуйте снова.", "Bulunduğunuz bölgeden kayıt ve giriş şu anda kullanılamıyor. Başka bir VPN veya proxy açıksa kapatıp tekrar deneyin.", "Sign-up and sign-in aren’t available from your region right now. If another VPN or proxy is on, turn it off and try again."],
        ["err.trialUsed"] = ["На этом компьютере пробный период уже использован. Выберите тариф на colitu.com, чтобы продолжить.", "Bu bilgisayarda deneme süresi daha önce kullanıldı. Devam etmek için colitu.com’dan bir paket seçin.", "The free trial was already used on this computer. Choose a plan at colitu.com to continue."],
        ["err.notVerified"] = ["Сначала подтвердите адрес электронной почты.", "Önce e-posta adresinizi doğrulayın.", "Please confirm your email address first."],

        // E-mail verification
        ["verify.kicker"] = ["ПОСЛЕДНИЙ ШАГ", "SON ADIM", "ONE LAST STEP"],
        ["verify.title"] = ["Подтвердите почту", "E-postanızı doğrulayın", "Confirm your email"],
        ["verify.sub"] = ["Мы отправили 6-значный код на {email}. Введите его, чтобы активировать аккаунт и бесплатный тариф.", "{email} adresine 6 haneli bir kod gönderdik. Hesabınızı ve ücretsiz planınızı açmak için kodu girin.", "We sent a 6-digit code to {email}. Enter it to activate your account and free plan."],
        ["verify.code"] = ["Код из письма", "E-postadaki kod", "Code from the email"],
        ["verify.submit"] = ["Подтвердить", "Doğrula", "Confirm"],
        ["verify.resend"] = ["Отправить код ещё раз", "Kodu tekrar gönder", "Send the code again"],
        ["verify.resendIn"] = ["Отправить снова через {n} с", "{n} sn sonra tekrar gönderebilirsiniz", "Send again in {n}s"],
        ["verify.sent"] = ["Новый код отправлен. Проверьте и папку «Спам».", "Yeni kod gönderildi. Gereksiz (spam) klasörünü de kontrol edin.", "A new code is on its way. Check your spam folder too."],
        ["verify.other"] = ["Войти в другой аккаунт", "Başka bir hesapla giriş yap", "Use a different account"],
        ["verify.hint"] = ["Код действует 15 минут. Письмо не пришло? Проверьте «Спам» или отправьте код ещё раз.", "Kod 15 dakika geçerlidir. E-posta gelmediyse spam klasörüne bakın veya kodu yeniden gönderin.", "The code is valid for 15 minutes. No email? Check spam or send the code again."],
        ["verify.web"] = ["Код можно ввести и на colitu.com: приложение продолжит само, как только почта будет подтверждена.", "Kodu colitu.com üzerinden de girebilirsiniz; doğrulandığında uygulama kendiliğinden devam eder.", "You can also enter the code on colitu.com; the app carries on by itself once your email is confirmed."],
        ["verify.done"] = ["Почта подтверждена. Добро пожаловать в Colitu!", "E-postanız doğrulandı. Colitu’ya hoş geldiniz!", "Email confirmed. Welcome to Colitu!"],
        ["verify.err.length"] = ["Введите все 6 цифр кода.", "Kodun 6 hanesini de girin.", "Enter all 6 digits of the code."],
        ["verify.err.invalid"] = ["Неверный код. Проверьте письмо и попробуйте снова.", "Kod hatalı. E-postayı kontrol edip tekrar deneyin.", "That code isn’t right. Check the email and try again."],
        ["verify.err.expired"] = ["Срок действия кода истёк. Отправьте новый.", "Kodun süresi doldu. Yeni bir kod isteyin.", "The code has expired. Request a new one."],
        ["verify.err.wait"] = ["Новый код можно запросить через минуту.", "Yeni kodu bir dakika sonra isteyebilirsiniz.", "You can request a new code in a minute."],
        ["verify.err.mail"] = ["Сейчас не удаётся отправить письмо. Попробуйте чуть позже или напишите в поддержку.", "Şu anda e-posta gönderilemiyor. Biraz sonra tekrar deneyin veya destekle iletişime geçin.", "We can’t send email right now. Try again shortly or contact support."],

        // End of a trial or plan, and devices over the plan's limit
        ["planEnd.trial"] = ["Пробный период закончится {date}.", "Denemeniz {date} tarihinde bitiyor.", "Your trial ends on {date}."],
        ["planEnd.paid"] = ["Ваш тариф закончится {date}.", "Paketiniz {date} tarihinde bitiyor.", "Your plan ends on {date}."],
        ["planEnd.over"] = ["{ends} После этого активным останется только {devices} — то, которым вы пользовались последним. Остальные будут приостановлены, но вы не выйдете из аккаунта.", "{ends} Sonrasında yalnızca {devices} aktif kalır: en son kullandığınız. Diğerleri durdurulur, oturumları kapanmaz.", "{ends} After that only {devices} stays active: the one you used most recently. The others are paused, not signed out."],
        ["planEnd.overPlain"] = ["{ends} Вы перейдёте на бесплатный тариф; устройство, которым вы пользовались последним, останется активным, остальные будут приостановлены, но вы не выйдете из аккаунта.", "{ends} Ücretsiz plana geçeceksiniz; en son kullandığınız cihaz aktif kalır, diğerleri durdurulur ama oturumları kapanmaz.", "{ends} You’ll move to the free plan; the device you used most recently stays active, the others are paused, not signed out."],
        ["planEnd.within"] = ["{ends} Вы перейдёте на бесплатный тариф.", "{ends} Ücretsiz plana geçeceksiniz.", "{ends} You’ll move to the free plan."],
        ["planEnd.hide"] = ["Скрыть на сегодня", "Bugünlük gizle", "Hide for today"],
        ["err.devicePaused"] = ["Это устройство приостановлено: тариф позволяет меньше устройств.", "Bu cihaz durduruldu: paketiniz daha az cihaza izin veriyor.", "This device is paused: your plan allows fewer devices."],
        ["paused.kicker"] = ["ЛИМИТ УСТРОЙСТВ", "CİHAZ SINIRI", "DEVICE LIMIT"],
        ["paused.title"] = ["Это устройство приостановлено", "Bu cihaz durduruldu", "This device is paused"],
        ["paused.body"] = ["Ваш тариф позволяет {limit}. Активно: {names}.", "Paketiniz {limit} için geçerli. Aktif: {names}.", "Your plan allows {limit}. Active: {names}."],
        ["paused.bodyNoNames"] = ["Ваш тариф позволяет {limit}; сейчас активно другое устройство.", "Paketiniz {limit} için geçerli; şu anda başka bir cihaz aktif.", "Your plan allows {limit}; another device is active right now."],
        ["paused.bodyNoLimit"] = ["Ваш тариф позволяет меньше устройств, чем вы используете. Активно: {names}.", "Paketiniz kullandığınızdan daha az cihaza izin veriyor. Aktif: {names}.", "Your plan allows fewer devices than you use. Active: {names}."],
        ["paused.bodyPlain"] = ["Ваш тариф позволяет меньше устройств, чем вы используете, поэтому это устройство приостановлено.", "Paketiniz kullandığınızdan daha az cihaza izin veriyor, bu yüzden bu cihaz durduruldu.", "Your plan allows fewer devices than you use, so this one is paused."],
        ["paused.hint"] = ["Если выбрать это устройство, активное сейчас будет приостановлено. Premium позволяет больше устройств.", "Bu cihazı kullanırsanız şu an aktif olan cihaz durdurulur. Premium daha fazla cihaza izin verir.", "Using this device pauses the one that is active now. Premium allows more devices."],
        ["paused.activate"] = ["Использовать это устройство", "Bunun yerine bu cihazı kullan", "Use this device instead"],
        ["paused.premium"] = ["Улучшить тариф", "Paketi yükselt", "Upgrade plan"],
        ["paused.retry"] = ["Проверить снова", "Tekrar kontrol et", "Check again"],
        ["account.paused"] = ["ПРИОСТАНОВЛЕНО", "DURDURULDU", "PAUSED"],
        ["account.activate"] = ["Активировать", "Etkinleştir", "Activate"],
        ["account.activated"] = ["Устройство активировано. Другое устройство приостановлено, если тариф не позволяет больше.", "Cihaz etkinleştirildi. Paketiniz izin vermiyorsa başka bir cihaz durduruldu.", "Device activated. Another device was paused if your plan allows no more."],
        ["account.security"] = ["Двухфакторная аутентификация", "İki faktörlü doğrulama", "Two-factor authentication"],
        ["mfa.err.invalidLeft"] = ["Неверный код. Осталось попыток: {n}.", "Kod hatalı. Kalan deneme: {n}.", "That code isn’t right. Attempts left: {n}."],
        ["paused.activated"] = ["Это устройство снова активно.", "Bu cihaz yeniden aktif.", "This device is active again."],

        // Two-factor authentication (turned on and managed on colitu.com)
        ["mfa.kicker"] = ["ДВУХЭТАПНАЯ ПРОВЕРКА", "İKİ ADIMLI DOĞRULAMA", "TWO-STEP VERIFICATION"],
        ["mfa.title"] = ["Введите код 2FA", "2FA kodunu girin", "Enter your 2FA code"],
        ["mfa.sub"] = ["Для {email} включена двухфакторная аутентификация. Откройте приложение-аутентификатор и введите 6-значный код.", "{email} için iki faktörlü kimlik doğrulama açık. Kimlik doğrulama uygulamanızı açın ve 6 haneli kodu girin.", "Two-factor authentication is on for {email}. Open your authenticator app and enter the 6-digit code."],
        ["mfa.code"] = ["Код из приложения", "Uygulamadaki kod", "Code from the app"],
        ["mfa.recoveryLabel"] = ["Код восстановления", "Kurtarma kodu", "Recovery code"],
        ["mfa.useRecovery"] = ["Использовать код восстановления", "Kurtarma kodu kullan", "Use a recovery code"],
        ["mfa.useApp"] = ["Ввести код из приложения", "Uygulamadaki kodu gir", "Enter the code from the app"],
        ["mfa.recoveryHint"] = ["Каждый код восстановления срабатывает один раз. Вы сохранили их, когда включали 2FA на colitu.com.", "Her kurtarma kodu bir kez çalışır. Kodları colitu.com’da 2FA’yı açarken kaydetmiştiniz.", "Each recovery code works once. You saved them when you turned on 2FA on colitu.com."],
        ["mfa.submit"] = ["Войти", "Giriş yap", "Sign in"],
        ["mfa.back"] = ["Назад ко входу", "Girişe dön", "Back to sign in"],
        ["mfa.hint"] = ["Двухфакторная аутентификация включается и настраивается на colitu.com в настройках аккаунта.", "İki faktörlü kimlik doğrulama colitu.com’da hesap ayarlarından açılır ve yönetilir.", "Two-factor authentication is turned on and managed on colitu.com, in your account settings."],
        ["mfa.err.length"] = ["Введите все 6 цифр кода.", "Kodun 6 hanesini de girin.", "Enter all 6 digits of the code."],
        ["mfa.err.recoveryEmpty"] = ["Введите код восстановления.", "Kurtarma kodunu girin.", "Enter a recovery code."],
        ["mfa.err.invalid"] = ["Неверный код. Проверьте приложение-аутентификатор и попробуйте снова.", "Kod hatalı. Kimlik doğrulama uygulamanızı kontrol edip tekrar deneyin.", "That code isn’t right. Check your authenticator app and try again."],
        ["mfa.err.expired"] = ["Время на ввод кода истекло. Введите пароль ещё раз.", "Kod girme süresi doldu. Şifrenizi yeniden girin.", "The sign-in step timed out. Enter your password again."],
        ["mfa.err.required"] = ["Для этого аккаунта нужна двухфакторная аутентификация. Попробуйте войти ещё раз.", "Bu hesap iki faktörlü kimlik doğrulama kullanıyor. Lütfen yeniden giriş yapmayı deneyin.", "This account uses two-factor authentication. Please try signing in again."],
        ["mfa.err.updateApp"] = ["Этот аккаунт защищён двухфакторной аутентификацией, а для неё нужна более новая версия Colitu. Обновите приложение и войдите снова.", "Bu hesap iki faktörlü kimlik doğrulamayla korunuyor ve bunun için Colitu’nun daha yeni bir sürümü gerekiyor. Uygulamayı güncelleyip yeniden giriş yapın.", "This account is protected by two-factor authentication, which needs a newer version of Colitu. Update the app and sign in again."],

        // Server categories
        ["cat.all"] = ["Все", "Tümü", "All"],
        ["cat.streaming"] = ["Стриминг", "Streaming", "Streaming"],
        ["cat.gaming"] = ["Игры", "Oyun", "Gaming"],
        ["cat.privacy"] = ["Приватность", "Gizlilik", "Privacy"],
        ["cat.speed"] = ["Скорость", "Hız", "Speed"],
        ["cat.torrent"] = ["Торренты", "Torrent", "Torrent"],
        ["cat.ai"] = ["ИИ", "Yapay zekâ", "AI"],
        ["cat.adblock"] = ["Блокировка рекламы", "Reklam engelleme", "Ad blocking"],
        ["cat.empty"] = ["В этой категории пока нет серверов.", "Bu kategoride henüz sunucu yok.", "No servers in this category yet."],

        // Live support
        ["nav.support"] = ["Поддержка", "Destek", "Support"],
        ["support.kicker"] = ["ПОДДЕРЖКА", "DESTEK", "SUPPORT"],
        ["support.title"] = ["Живая поддержка", "Canlı destek", "Live support"],
        ["support.sub"] = ["Напишите нам прямо из приложения. Отвечает живой человек, обычно в течение часа.", "Bize doğrudan uygulamadan yazın. Gerçek bir kişi, genellikle bir saat içinde yanıt verir.", "Write to us right from the app. A real person answers, usually within an hour."],
        ["support.new"] = ["Новое обращение", "Yeni talep", "New request"],
        ["support.empty"] = ["Обращений пока нет. Опишите проблему — мы поможем.", "Henüz talebiniz yok. Sorununuzu anlatın, yardımcı olalım.", "No requests yet. Tell us what’s wrong and we’ll help."],
        ["support.pick"] = ["Выберите обращение слева или создайте новое.", "Soldan bir talep seçin veya yeni bir talep oluşturun.", "Pick a request on the left or start a new one."],
        ["support.subject"] = ["Тема", "Konu", "Subject"],
        ["support.subjectHint"] = ["Например: не подключается к серверу", "Örn: Sunucuya bağlanamıyorum", "For example: can’t connect to a server"],
        ["support.message"] = ["Сообщение", "Mesaj", "Message"],
        ["support.messageHint"] = ["Опишите, что происходит и что вы уже пробовали…", "Ne olduğunu ve neler denediğinizi yazın…", "Describe what’s happening and what you’ve tried…"],
        ["support.reply"] = ["Напишите ответ…", "Yanıtınızı yazın…", "Write a reply…"],
        ["support.send"] = ["Отправить", "Gönder", "Send"],
        ["support.attach"] = ["Прикрепить файл", "Dosya ekle", "Attach a file"],
        ["support.attachHint"] = ["Скриншоты, PDF, TXT, ZIP · до 10 МБ", "Ekran görüntüsü, PDF, TXT, ZIP · en fazla 10 MB", "Screenshots, PDF, TXT, ZIP · up to 10 MB"],
        ["support.diagnostics"] = ["Приложить диагностику", "Tanılama bilgisini ekle", "Include diagnostics"],
        ["support.diagnosticsHint"] = ["Версия приложения и Linux, состояние подключения, последние ошибки и журнал. Без истории посещений.", "Uygulama ve Linux sürümü, bağlantı durumu, son hatalar ve günlük kaydı. Gezinme geçmişi gönderilmez.", "App and Linux version, connection state, recent errors and the log. No browsing history."],
        ["support.create"] = ["Отправить обращение", "Talebi gönder", "Send request"],
        ["support.cancel"] = ["Отмена", "Vazgeç", "Cancel"],
        ["support.you"] = ["Вы", "Siz", "You"],
        ["support.team"] = ["Поддержка Colitu", "Colitu Destek", "Colitu Support"],
        ["support.status.waiting"] = ["ЖДЁТ ОТВЕТА", "YANIT BEKLİYOR", "AWAITING REPLY"],
        ["support.status.open"] = ["В РАБОТЕ", "İNCELENİYOR", "IN PROGRESS"],
        ["support.status.resolved"] = ["РЕШЕНО", "ÇÖZÜLDÜ", "RESOLVED"],
        ["support.status.closed"] = ["ЗАКРЫТО", "KAPANDI", "CLOSED"],
        ["support.closed"] = ["Обращение закрыто. Создайте новое, если нужна помощь.", "Bu talep kapatıldı. Yardım gerekirse yeni bir talep açın.", "This request is closed. Start a new one if you need help."],
        ["support.newReply"] = ["Новый ответ поддержки", "Destekten yeni yanıt", "New reply from support"],
        ["support.sent"] = ["Обращение отправлено. Мы ответим здесь и по почте.", "Talebiniz gönderildi. Buradan ve e-postayla yanıt vereceğiz.", "Request sent. We’ll answer here and by email."],
        ["support.err.subject"] = ["Укажите тему и сообщение.", "Konu ve mesajı yazın.", "Add a subject and a message."],
        ["support.err.file"] = ["Файл больше 10 МБ или такой тип не поддерживается.", "Dosya 10 MB’tan büyük veya bu tür desteklenmiyor.", "The file is over 10 MB or the type isn’t supported."],
        ["support.err.files"] = ["Можно прикрепить не больше 5 файлов.", "En fazla 5 dosya ekleyebilirsiniz.", "You can attach up to 5 files."],
        ["support.err.unavailable"] = ["Поддержка в приложении временно недоступна. Напишите на support@colitu.com.", "Uygulama içi destek geçici olarak kapalı. support@colitu.com adresine yazın.", "In-app support is unavailable right now. Write to support@colitu.com."],
        ["support.help"] = ["Справочный центр", "Yardım merkezi", "Help centre"],
        ["err.noPlan"] = ["Тариф не активен. Выберите тариф, чтобы подключиться.", "Paketiniz aktif değil. Bağlanmak için bir paket seçin.", "Your plan isn’t active. Choose a plan to connect."],
        ["err.quota"] = ["Лимит трафика на этот период исчерпан.", "Bu dönemin trafik kotası doldu.", "You’ve used this period’s traffic allowance."],
        ["err.noServers"] = ["Сейчас нет доступных серверов. Попробуйте чуть позже.", "Şu anda uygun sunucu yok. Biraz sonra tekrar deneyin.", "No server is available right now. Please try again shortly."],
        ["err.network"] = ["Нет связи с сервером Colitu. Проверьте интернет.", "Colitu sunucusuna ulaşılamadı. İnternet bağlantınızı kontrol edin.", "Can’t reach Colitu. Check your internet connection."],
        ["err.unreachable"] = ["Сервер не отвечает. Попробуйте другую локацию.", "Sunucu yanıt vermiyor. Başka bir konum deneyin.", "The server isn’t responding. Try another location."],
        ["err.admin"] = ["Для режима TUN нужен пароль администратора (sudo). Введите его или выберите режим «Прокси».", "TUN modu için yönetici (sudo) parolası gerekiyor. Parolayı girin ya da Proxy modunu seçin.", "TUN mode needs the administrator (sudo) password. Enter it or switch to Proxy mode."],
        ["err.core"] = ["Файлы VPN повреждены. Переустановите Colitu.", "VPN dosyaları eksik. Colitu’yu yeniden kurun.", "VPN files are missing. Please reinstall Colitu."],
        ["err.port"] = ["Локальный порт занят другой программой VPN или прокси. Закройте её и повторите.", "Yerel bağlantı noktası başka bir VPN/proxy programı tarafından kullanılıyor. Kapatıp tekrar deneyin.", "Another VPN or proxy app is using the local port. Close it and try again."],
        ["err.proxy"] = ["Системный прокси нельзя настроить в этой среде рабочего стола. Включите режим TUN.", "Sistem proxy'si bu masaüstü ortamında ayarlanamıyor. TUN modunu açın.", "The system proxy can’t be set on this desktop. Switch to TUN mode."],
        ["err.tun"] = ["Не удалось включить адаптер TUN. Попробуйте режим «Прокси».", "TUN bağdaştırıcısı açılamadı. Proxy modunu deneyin.", "The TUN adapter didn’t start. Try Proxy mode."],
        ["err.generic"] = ["Что-то пошло не так. Попробуйте ещё раз.", "Bir şeyler ters gitti. Tekrar deneyin.", "Something went wrong. Please try again."],
        ["err.payment"] = ["Не удалось создать платёж. Попробуйте другой способ оплаты.", "Ödeme oluşturulamadı. Başka bir ödeme yöntemi deneyin.", "The payment couldn’t be created. Try another payment method."],
        ["err.reconnectFailed"] = ["Соединение потеряно. Нажмите «Подключить», чтобы восстановить.", "Bağlantı koptu. Yeniden bağlanmak için “Bağlan”a basın.", "The connection dropped. Press Connect to restore it."],
        ["info.reconnected"] = ["Соединение восстановлено", "Bağlantı yeniden kuruldu", "Connection restored"],
        ["info.disconnected"] = ["VPN отключён", "VPN bağlantısı kesildi", "VPN disconnected"],
        ["warn.otherVpn"] = ["Похоже, запущен другой VPN (например, Clash). Если соединение не работает, отключите его.", "Başka bir VPN uygulaması (ör. Clash) açık görünüyor. Bağlantı çalışmazsa onu kapatın.", "Another VPN app (for example Clash) seems to be running. If the connection doesn’t work, turn it off."],
        ["info.killSwitch"] = ["Соединение прервалось. Kill switch заблокировал интернет, переподключаемся…", "Bağlantı koptu. Kill switch interneti engelledi, yeniden bağlanılıyor…", "Connection lost. The kill switch blocked the internet while we reconnect…"],
        ["err.killSwitch"] = ["Не удалось включить kill switch. Он работает в режиме TUN с паролем администратора.", "Kill switch açılamadı. TUN modunda, yönetici parolasıyla çalışır.", "The kill switch couldn’t be turned on. It works in TUN mode with the administrator password."],

        // Linux
        ["sudo.title"] = ["Пароль администратора", "Yönetici parolası", "Administrator password"],
        ["sudo.body"] = ["Для режима TUN Colitu запускает ядро VPN от имени root. Введите пароль sudo; он хранится только в памяти до выхода из приложения.", "TUN modunda Colitu VPN çekirdeğini root olarak çalıştırır. sudo parolanızı girin; parola yalnızca uygulama kapanana kadar bellekte tutulur.", "In TUN mode Colitu runs the VPN core as root. Enter your sudo password; it is kept in memory only until the app closes."],
        ["sudo.ok"] = ["Продолжить", "Devam", "Continue"],
        ["sudo.cancel"] = ["Отмена", "İptal", "Cancel"],
        ["sudo.wrong"] = ["Неверный пароль. Попробуйте ещё раз.", "Parola yanlış. Tekrar deneyin.", "Wrong password. Please try again."],
        ["sudo.checking"] = ["Проверяем…", "Kontrol ediliyor…", "Checking…"],
        ["sudo.bodyKillSwitch"] = ["Правила kill switch принадлежат системе (root). Введите пароль sudo, чтобы изменить их; он хранится только в памяти до выхода из приложения.", "Kill switch kuralları sisteme (root) aittir. Bunları değiştirmek için sudo parolanızı girin; parola yalnızca uygulama kapanana kadar bellekte tutulur.", "The kill switch rules belong to the system (root). Enter your sudo password to change them; it is kept in memory only until the app closes."],
        ["ksr.kicker"] = ["KILL SWITCH", "KILL SWITCH", "KILL SWITCH"],
        ["ksr.title"] = ["Интернет заблокирован kill switch", "İnternet kill switch tarafından engellendi", "The kill switch is blocking the internet"],
        ["ksr.body"] = ["Colitu неожиданно закрылся, пока защищал вас. Чтобы ваш трафик не утёк мимо VPN, kill switch продолжает блокировать интернет. Переподключитесь к VPN или отключите защиту, чтобы пользоваться интернетом без неё.", "Colitu sizi korurken beklenmedik şekilde kapandı. Trafiğiniz VPN dışına sızmasın diye kill switch interneti engellemeye devam ediyor. VPN’e yeniden bağlanın ya da interneti korumasız kullanmak için korumayı kapatın.", "Colitu closed unexpectedly while it was protecting you. To keep your traffic from leaking outside the VPN, the kill switch is still blocking the internet. Reconnect to the VPN, or turn protection off to use the internet without it."],
        ["ksr.bodyOff"] = ["Colitu неожиданно закрылся, когда kill switch был включён, и он всё ещё блокирует интернет. Отключите защиту, чтобы вернуть интернет.", "Colitu, kill switch açıkken beklenmedik şekilde kapandı ve kill switch hâlâ interneti engelliyor. İnterneti geri almak için korumayı kapatın.", "Colitu closed unexpectedly while the kill switch was on, and it is still blocking the internet. Turn protection off to get your internet back."],
        ["ksr.password"] = ["Для этого нужен пароль администратора (sudo).", "Bunun için yönetici (sudo) parolası gerekir.", "This needs your administrator (sudo) password."],
        ["ksr.reconnect"] = ["Переподключиться", "Yeniden bağlan", "Reconnect"],
        ["ksr.turnOff"] = ["Отключить защиту", "Korumayı kapat", "Turn off protection"],
        ["ksr.later"] = ["Не сейчас", "Şimdi değil", "Not now"],
        ["ksr.removed"] = ["Защита отключена. Интернет снова работает.", "Koruma kapatıldı. İnternetiniz geri geldi.", "Protection is off. Your internet is back."],
        ["ksr.failed"] = ["Не удалось снять блокировку. Выполните в терминале: colitu-vpn --colitu-cleanup", "Engel kaldırılamadı. Terminalde şunu çalıştırın: colitu-vpn --colitu-cleanup", "The block couldn’t be removed. Run this in a terminal: colitu-vpn --colitu-cleanup"],
        ["ksr.exitNotice"] = ["Kill switch всё ещё блокирует интернет. Откройте Colitu, чтобы переподключиться или отключить защиту.", "Kill switch hâlâ interneti engelliyor. Yeniden bağlanmak veya korumayı kapatmak için Colitu’yu açın.", "The kill switch is still blocking the internet. Open Colitu to reconnect or turn protection off."],
        ["home.sub.crashBlocked"] =["Colitu неожиданно закрылся, и kill switch всё ещё блокирует интернет. Переподключитесь или отключите защиту.", "Colitu beklenmedik şekilde kapandı ve kill switch hâlâ interneti engelliyor. Yeniden bağlanın veya korumayı kapatın.", "Colitu closed unexpectedly and the kill switch is still blocking the internet. Reconnect, or turn protection off."],
        ["settings.killSwitchTunOnly"] = ["На Linux kill switch работает только в режиме TUN.", "Linux’ta kill switch yalnızca TUN modunda çalışır.", "On Linux the kill switch works in TUN mode only."],
        ["confirm.remove"] = ["Удалить", "Kaldır", "Remove"],
        ["confirm.cancel"] = ["Отмена", "İptal", "Cancel"],
        ["update.manual"] = ["Эта установка не поддерживает автообновление. Откроется страница загрузки.", "Bu kurulum otomatik güncellemeyi desteklemiyor. İndirme sayfası açılacak.", "This installation can’t update itself. The download page will open."],
        ["update.auth"] = ["Подтвердите установку обновления в окне системы.", "Güncellemenin kurulmasını sistem penceresinde onaylayın.", "Confirm the update installation in the system prompt."],

        // Tray
        ["tray.open"] = ["Открыть Colitu", "Colitu’yu aç", "Open Colitu"],
        ["tray.connect"] = ["Подключить", "Bağlan", "Connect"],
        ["tray.disconnect"] = ["Отключить", "Bağlantıyı kes", "Disconnect"],
        ["tray.exit"] = ["Выйти из Colitu", "Colitu’dan çık", "Quit Colitu"],
        ["tray.hidden"] = ["Colitu работает в трее. Защита остаётся включённой.", "Colitu tepside çalışıyor. Koruma açık kalır.", "Colitu is still running in the tray. Protection stays on."],

        // Multihop routes and rotating IP (1.2.0)
        ["multihop.section"] = ["Мультихоп", "Çoklu atlama", "Multihop"],
        ["multihop.sectionHint"] = ["Трафик идёт через два сервера: сначала входной, затем выходной. Работает только по VLESS; пинг показан до входного сервера.", "Trafik iki sunucudan geçer: önce giriş, sonra çıkış. Yalnızca VLESS ile çalışır; ping yalnızca giriş sunucusu içindir.", "Traffic passes two servers: the entry first, then the exit. Works over VLESS only; ping is measured to the entry server."],
        ["multihop.ping"] = ["Оценка · +1 узел", "Tahmini · +1 atlama", "Estimated · +1 hop"],
        ["multihop.sub"] = ["Двойной VPN · {entry} → {exit}", "Çift VPN · {entry} → {exit}", "Double VPN · {entry} → {exit}"],
        ["multihop.home"] = ["Вход {entry} → выход {exit}", "Giriş {entry} → çıkış {exit}", "Entry {entry} → Exit {exit}"],
        ["multihop.gone"] = ["Этот маршрут больше недоступен. Список обновлён — выберите другой.", "Bu rota artık kullanılamıyor. Liste yenilendi; başka bir tane seçin.", "This route is no longer available. The list was refreshed; pick another one."],
        ["multihop.needsVless"] = ["Мультихоп работает только по VLESS, а этот маршрут не предлагает такой транспорт.", "Çoklu atlama yalnızca VLESS ile çalışır, bu rota böyle bir aktarım sunmuyor.", "Multihop works over VLESS only, and this route offers no VLESS transport."],
        ["rotation.title"] = ["Меняющийся IP", "Dönen IP", "Rotating IP"],
        ["rotation.hint"] = ["IP-адрес и страна выхода меняются по расписанию без отключения от VPN. Уже открытые соединения остаются на прежнем выходе. Работает только по VLESS.", "Çıkış IP adresiniz ve ülkeniz bağlantı kesilmeden belirli aralıklarla değişir. Açık bağlantılar eski çıkışta kalır. Yalnızca VLESS ile çalışır.", "Your exit IP address and country change on a schedule without disconnecting. Open connections stay on their old exit. Works over VLESS only."],
        ["rotation.off"] = ["Выкл.", "Kapalı", "Off"],
        ["rotation.minutes"] = ["{n} мин", "{n} dk", "{n} min"],
        ["rotation.countries"] = ["Страны выхода", "Çıkış ülkeleri", "Exit countries"],
        ["rotation.countriesHint"] = ["Отметьте минимум две страны. Россия не входит в набор по умолчанию.", "En az iki ülke işaretleyin. Rusya varsayılan sette yer almaz.", "Tick at least two countries. Russia is not in the default set."],
        ["rotation.err.few"] = ["Отметьте минимум две страны.", "En az iki ülke işaretleyin.", "Tick at least two countries."],
        ["rotation.needsVless"] = ["Смена IP работает только по VLESS, а этот сервер не предлагает такой транспорт.", "IP değişimi yalnızca VLESS ile çalışır, bu sunucu böyle bir aktarım sunmuyor.", "Rotation needs VLESS, and this server offers no VLESS transport."],
        ["rotation.home"] = ["Выход: {exit} · смена через {time}", "Çıkış: {exit} · değişimine {time}", "Exit: {exit} · changes in {time}"],
        ["rotation.homeDue"] = ["Выход: {exit} · меняется сейчас", "Çıkış: {exit} · şimdi değişiyor", "Exit: {exit} · changing now"],
        ["account.manualConfig"] = ["Ручная настройка", "Manuel yapılandırma", "Manual configuration"],
    };
}

/// <summary>
/// XAML: <c>Text="{s:T home.connect}"</c>: a binding that follows language changes.
/// One observable source per key, so repeated lookups do not pile up subscriptions.
/// </summary>
public sealed class T : Avalonia.Markup.Xaml.MarkupExtension
{
    private static readonly Dictionary<string, LocText> Cache = [];

    public T() { }

    public T(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (!Cache.TryGetValue(Key, out var source))
        {
            Cache[Key] = source = new LocText(Key);
        }
        return new Avalonia.Data.Binding(nameof(LocText.Value)) { Source = source, Mode = Avalonia.Data.BindingMode.OneWay };
    }
}

/// <summary>One localized string that raises a change when the language switches.</summary>
public sealed class LocText : INotifyPropertyChanged
{
    private readonly string _key;

    public LocText(string key)
    {
        _key = key;
        Loc.I.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Value => Loc.I[_key];
}

/// <summary>
/// Colitu's own name of a panel transport ("Fast" for Hysteria2 and so on):
/// the technical protocol names stay out of the UI.
/// </summary>
public static class ColituTransportNames
{
    private static readonly HashSet<string> Known = ["hysteria2", "vless-reality", "vless-xhttp", "trojan", "shadowsocks"];

    public static string Of(string? protocol, Loc loc)
    {
        if (string.IsNullOrEmpty(protocol)) return "";
        return loc[Known.Contains(protocol) ? $"transport.{protocol}" : "transport.other"];
    }
}
