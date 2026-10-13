# Yayın hazırlama

Bu adımlar bakımcı içindir. GitHub Actions kullanılmaz; bir belgenin veya JSON ayar dosyasının depoya eklenmesi testlerin ya da GitHub korumalarının etkin olduğu anlamına gelmez. Eski commit ve sürüm etiketlerini görünümü sadeleştirmek için yeniden yazmayın.

## Kaynak, lisans ve doğrulama

1. Sürümü README, kullanım notları, changelog, uygulama metadata'sı ve derleme mesajında birlikte güncelleyin. Resmî Discord/profil/ağ akışını değiştiren bir düzenlemeyi yalnız sürüm/dağıtım düzeltmesi diye sunmayın.
2. [CONTRIBUTING.md](../CONTRIBUTING.md) içindeki C#, Node ve kısayol kontrollerini izin verilen Windows ortamında çalıştırın. Motor için `-RunSourceTests`, yapılandırma için açık `-RunChecks` seçeneğini kullanın. Engellenen veya atlanan grup geçmemiştir; Windows güvenlik ayarlarını düşürmeyin ya da farklı yükleme yöntemi kullanmayın.
3. Dağıtılan motorun gerçek Go modülleri, wrapper/native kaynakları ve lisansları için envanteri yenileyin. Örneğin, sabit motor hash'iyle:

```powershell
python scripts/Collect-EngineNotices.py --engine engine/sing-box.exe --expected-sha256 8bb84e511d9c4dd86e7685c9654d25248b7ff23ea0226fc52a64f9a4066587a0
```

Bu hash `1.14.3-discordaccess.2` içindir; motor değişirse betik, uygulama pinleri ve manifest birlikte değişmelidir. Go modülünün üst lisansını tek başına native kaynaklar için yeterli saymayın. Eksik veya doğrulanmamış kaynak/lisansla paket yayımlamayın.

4. Son kaynak değişikliklerini commit edin; paketleme için tam commit'i kaydedin. Son derleme girdilerinden `Build.cmd` ile yeni `DiscordAccess.exe` hazırlayın. Doğrulama notuna kontrol edilen commit, komutlar, sonuçlar ve çalışmayan grupları yazın. Gerçek hesap, ses/yayın, tepsi ve Windows yeniden başlatma kontrollerini ayrı belirtin.

## Dört yayın dosyasını hazırlama

Python 3.11 veya üzeri gerekir. Aşağıdaki örnekte `ENGINE_BUILD_COMMIT` ve kanıt klasörünü gerçek başarılı elle motor derlemesinden alın. Kaynak/yayın commit'i son HEAD'dir; motor kanıt commit'i, betik/kilit/kaynak byte'ları değişmemişse daha önceki doğrulanmış commit olabilir. Paketleyici bu ayrımı kontrol eder; eski kayıt değişmiş kodun test başarısı sayılmaz.

```powershell
$releaseCommit = (git rev-parse HEAD).Trim()
$engineBuildCommit = 'ENGINE_BUILD_COMMIT'
$buildEvidence = 'work/security-engine-build-GERCEK_KIMLIK/artifacts'
$verifiedCache = 'work/license-source-cache'
$prepareRoot = Join-Path 'work' ('release-v4.21-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $prepareRoot | Out-Null
$sourceZip = Join-Path $prepareRoot 'DiscordAccess-v4.21-engine-source.zip'
$assets = Join-Path $prepareRoot 'assets'
$receipt = Join-Path $prepareRoot 'release-validation-local.json'

python scripts/Package-EngineSource.py --repo . --version 4.21 --commit $releaseCommit --build-evidence $buildEvidence --evidence-commit $engineBuildCommit --cache-dir $verifiedCache --output $sourceZip
python scripts/Package-Release.py --repo . --version 4.21 --commit $releaseCommit --engine engine/sing-box.exe --source $sourceZip --build-evidence $buildEvidence --output-dir $assets --receipt $receipt
```

Önceki doğrulanmış kaynak paketinden arşivleri almak gerekiyorsa ilk komuta `--source-companion ONCEKI_KAYNAK_ZIP` eklenebilir. Bu, güncel başarılı elle derleme kanıtının yerine geçmez. Native arşivler de collector'ın beklediği cache adları ve hash'leriyle bulunmalıdır.

Aynı girdilerle hazırlanmış dosyaları yeniden doğrulayın:

```powershell
python scripts/Package-EngineSource.py --repo . --version 4.21 --commit $releaseCommit --build-evidence $buildEvidence --evidence-commit $engineBuildCommit --cache-dir $verifiedCache --output $sourceZip --validate-only
python scripts/Package-Release.py --repo . --version 4.21 --commit $releaseCommit --engine engine/sing-box.exe --source $sourceZip --build-evidence $buildEvidence --output-dir $assets --receipt $receipt --validate-only
```

İlk hazırlamada `--source-companion` kullanıldıysa kaynak doğrulama komutuna da aynı girdiyi ekleyin. Her iki komut, mevcut çıktıların beklenen byte'larla aynı olduğunu kontrol eder ve dosya değiştirmez. Sıfır çıkış koduyla birlikte `existing_output_verified: true` sonucunu kaydedin. Çıktı yolu henüz yoksa `--validate-only` yalnız beklenen hash'leri hesaplar; bu, hazırlanmış yayın dosyalarının doğrulandığı anlamına gelmez. Hazır uygulama `DiscordAccess.exe` üzerinden alınır; motor veya uygulama paketleyiciler tarafından çalıştırılmaz.

Yalnız şu dört dosya Release'e eklenir:

- `DiscordAccess-v4.21.zip`
- `DiscordAccess-v4.21-engine-source.zip`
- `engine-source-manifest.json`
- `SHA256SUMS.txt`

Yerel `$receipt`, test raporları, ham loglar ve cache klasörü yayın dosyası değildir. Receipt makine yolları içerebilir; olduğu gibi paylaşmayın. Runtime ZIP test/profil/kurtarma verisi içermemeli; `LISANSLAR.txt` uygulama, motor, native ve bağımlılık bildirimlerini korumalıdır. ZIP/hash kontrolü kod imzası değildir.

## GitHub yayını ve eski indirmeler

Doğrulanmış dört dosyayla, hedefi tam kaynak commit'i olan bir taslak Release hazırlayın. Yayın notu sürüm kapsamını, yükseltme adımlarını, motor pinini, tamamlanan doğrulamayı ve kalan canlı testleri belirtmelidir. Var olan bir etiketi taşımayın; yeni sürüm etiketi kullanın. Dosya adları ve hash'ler kontrol edildikten sonra taslağı yayımlayıp güncel sürüm olarak işaretleyin.

Eksik lisans/kaynak bildirimi içeren eski hazır ikili indirmeleri geri çekin veya tam ve doğrulanmış dağıtım malzemeleriyle düzeltin. v4.16–v4.20 etiketleri ve kaynak geçmişi korunur; eski etiketin bulunması o ikili paketin desteklendiği anlamına gelmez. Geçmiş güvenlik sınırı veya kurulum yönergeleri için güncel README/SECURITY politikasını esas alın.

Tamamlanmış topic dallarını yalnız değişiklikleri `main` içinde doğrulandıktan ve açık iş/PR kalmadıktan sonra kaldırın. Squash/cherry-pick geçmişinde yalnız `git branch --merged` sonucuna güvenmeyin; PR durumu ve değişiklik eşdeğerliğini inceleyin. Release etiketlerini silmeyin veya force-push ile geçmişi yeniden yazmayın.

## Public geçişinden sonra etkinleştirilecek korumalar

Depo şu anda private tutulur. Public geçişi ayrı bir bakımcı kararıdır; bu checklist tek başına görünürlüğü değiştirmez. Mevcut GitHub planı private `main` korumasını reddediyorsa ayar dosyası korumayı etkinleştirmiş sayılmaz.

Public geçişi onaylandığında:

1. **Private vulnerability reporting** özelliğini etkinleştirin; [özel rapor yolu](https://github.com/alpertknn/DiscordAccess/security/advisories/new) ve Report a vulnerability düğmesinin çalıştığını doğrulayın. `SECURITY.md` etkin olmayan bir kanalı varmış gibi sunmamalıdır.
2. `.github/main-protection.json` politikasını GitHub'a uygulayın ve sunucudaki sonucu okuyun:

```powershell
gh api --method PUT repos/alpertknn/DiscordAccess/branches/main/protection --input .github/main-protection.json
gh api repos/alpertknn/DiscordAccess/branches/main/protection
```

Dosya yalnız deklaratif niyettir; başarılı API yanıtı ve okunan uzak ayar korumanın kanıtıdır. Politikada PR, en az bir kabul edilen başka inceleyici onayı, admin'lere uygulama, görüşmelerin çözülmesi ve force-push/dal silme engeli bulunmalıdır. Kendi PR'nize onay vererek bu gereksinimi karşılayamazsınız. GitHub Actions kapalı olduğundan olmayan Actions kontrollerini zorunlu status check yapmayın.

3. Repo About/topics, güncel Release, dört indirme dosyası, kaynak/lisans bağlantıları ve issue formlarını public görünümde kontrol edin. Otomatik merged-branch silme ayarı geçmişi veya release etiketlerini silmez.

API reddini veya eksik kontrolü başarılı olarak raporlamayın. Uzak ayarlar henüz uygulanamıyorsa hangi adımın beklediğini yayın notundan ayrı bakım kaydında belirtin.
