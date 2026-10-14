# Güvenlik bildirimi

## Desteklenen sürümler

Güvenlik düzeltmeleri en son yayımlanan sürüm için değerlendirilir. v4.22 yayımlandığında güncel sürüm odur; v4.20 ve önceki sürümler için yeni düzeltme desteği verilmez. Eski etiketler kaynak geçmişini korur ve güvenli/güncel ikili dağıtım önerisi sayılmaz.

Normal kullanım sorunları için [Issues](https://github.com/alpertknn/DiscordAccess/issues) şablonlarını kullanın. Güvenlik açığının ayrıntılarını herkese açık issue, PR, yorum veya log eki olarak paylaşmayın.

## Özel bildirim yolu

Depo private iken GitHub'ın public depolar için özel güvenlik bildirimi özelliğinin etkin olduğu varsayılmamalıdır. Public geçişinden sonra bakımcı **Private vulnerability reporting** özelliğini etkinleştirmeli ve aşağıdaki yolu doğrulamalıdır:

[Özel güvenlik raporu / Report a vulnerability](https://github.com/alpertknn/DiscordAccess/security/advisories/new)

Bu sayfa veya **Report a vulnerability** düğmesi henüz yoksa hassas ayrıntı göndermeyin. Bakımcıdan yalnız özel bildirim kanalının etkinleştirilmesini isteyin; bu talebe açık, token veya kişisel veri eklemeyin. Bu belge, özelliğin private depoda şu anda etkin olduğunu iddia etmez.

Rapora yalnız gereken bilgileri ekleyin:

- Etkilenen sürüm/commit ve Windows sürümü.
- Beklenen güven sınırı, gözlenen davranış ve etkisi.
- Sentetik verilerle kısa yeniden üretim adımları.
- Varsa kişisel verilerden arındırılmış düzeltme veya test önerisi.

Discord token'ı, parola, profil, `journal.json`, `.preserved` yedek, tam kullanıcı yolu, IP veya imzalı ek dosyası URL'si göndermeyin. Gerçek hesaba, başkasının verisine veya sisteme zarar veren deneme yapmayın. İnceleme gönüllü bakımcı çalışmasıdır; sabit yanıt/düzeltme süresi veya ödül programı vaat edilmez.

Teknik kapsam ve bilinen sınırlar [SECURITY-NOTES.md](docs/SECURITY-NOTES.md) içindedir. Proje Discord'un resmî ürünü değildir; imzasızdır ve Windows güvenlik kontrollerini atlamaz.

For security vulnerabilities, use the private reporting link only after the maintainer enables it for the public repository. Do not post sensitive details in public issues or pull requests. If reporting is unavailable, request a private channel without including the vulnerability or personal data.
