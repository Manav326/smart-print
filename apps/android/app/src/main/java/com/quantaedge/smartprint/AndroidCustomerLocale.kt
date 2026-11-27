package com.quantaedge.smartprint

import android.content.Context

/**
 * Customer-visible Android copy only. Internal identifiers, backend values,
 * diagnostics and document contents are intentionally never translated.
 */
internal object AndroidCustomerLocale {
    private const val PREFS = "smartprint.android.customer-ui"
    private const val KEY = "locale"

    const val ENGLISH = "english"
    const val HINDI = "hindi"
    const val HINGLISH = "hinglish"

    fun current(context: Context): String =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            .getString(KEY, ENGLISH)
            ?.takeIf { it in setOf(ENGLISH, HINDI, HINGLISH) } ?: ENGLISH

    fun save(context: Context, locale: String) {
        if (locale in setOf(ENGLISH, HINDI, HINGLISH)) {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().putString(KEY, locale).apply()
        }
    }

    fun label(context: Context): String = when (current(context)) {
        HINDI -> "भाषा · हिन्दी"
        HINGLISH -> "Language · Hinglish"
        else -> "Language · English"
    }

    fun translate(context: Context, source: String): String {
        val locale = current(context)
        if (locale == ENGLISH) return source
        val hindi = hindiCopy[source]
        val hinglish = hinglishCopy[source]
        return when (locale) {
            HINDI -> hindi ?: source
            HINGLISH -> hinglish ?: source
            else -> source
        }
    }

    /**
     * The embedded customer/shop journey uses the same locale key as the
     * portal LanguageProvider. Install before first paint on the trusted origin.
     */
    fun documentStartScript(context: Context): String {
        val locale = current(context)
        return """(function(){try{var l='$locale';localStorage.setItem('smartprint-ui-locale',l);document.cookie='smartprint-ui-locale='+l+'; Path=/; Max-Age=31536000; SameSite=Lax'+(location.protocol==='https:'?'; Secure':'');window.dispatchEvent(new StorageEvent('storage',{key:'smartprint-ui-locale',newValue:l}));}catch(e){}})();"""
    }

    private val hindiCopy = mapOf(
        "Language" to "भाषा", "Choose the language for customer-facing app screens." to "ग्राहक को दिखने वाली ऐप स्क्रीन की भाषा चुनें।",
        "My Files" to "मेरी फ़ाइलें", "Print at a shop" to "शॉप पर प्रिंट करें",
        "Home" to "होम", "Files" to "फ़ाइलें", "Print" to "प्रिंट", "Activity" to "गतिविधि",
        "Account" to "अकाउंट", "Back" to "वापस", "Back to Home" to "होम पर जाएँ",
        "Appearance" to "दिखावट", "Choose how SmartPrint looks on this device. This setting is kept when you sign out." to "इस डिवाइस पर SmartPrint की दिखावट चुनें। साइन आउट करने पर भी यह सेटिंग बनी रहेगी।",
        "Light theme" to "लाइट थीम", "Dark theme" to "डार्क थीम",
        "Sign in" to "साइन इन", "Sign out" to "साइन आउट", "Sign out?" to "साइन आउट करें?",
        "Create account" to "अकाउंट बनाएँ", "Name" to "नाम", "Email address" to "ईमेल पता",
        "Password" to "पासवर्ड", "Password · 12+ chars" to "पासवर्ड · कम से कम 12 अक्षर",
        "Complete the required fields." to "कृपया ज़रूरी जानकारी भरें।",
        "Print history" to "प्रिंट हिस्ट्री", "Local files stay on this phone." to "लोकल फ़ाइलें इसी फ़ोन में रहेंगी।",
        "Guest access is ready" to "गेस्ट एक्सेस तैयार है", "Smart Print customer" to "Smart Print ग्राहक",
        "Your next print starts here." to "आपकी अगली प्रिंटिंग यहीं से शुरू होती है।",
        "Everything ready for your next print." to "आपकी अगली प्रिंटिंग के लिए सब तैयार है।",
        "Your files stay private. Print when ready." to "आपकी फ़ाइलें निजी रहती हैं। तैयार होने पर प्रिंट करें।",
        "Find a print shop" to "प्रिंट शॉप खोजें", "Scan the shop QR to connect" to "जुड़ने के लिए शॉप QR स्कैन करें",
        "Connected · tap to manage" to "कनेक्टेड · मैनेज करने के लिए टैप करें",
        "Manage connected shop" to "कनेक्टेड शॉप मैनेज करें",
        "ID Studio" to "ID स्टूडियो", "Photo Studio" to "फोटो स्टूडियो",
        "Your space" to "आपकी जगह", "Recent files" to "हाल की फ़ाइलें", "View library  ›" to "लाइब्रेरी देखें  ›",
        "Scan document" to "दस्तावेज़ स्कैन करें", "Choose files" to "फ़ाइलें चुनें",
        "Your files will appear here" to "आपकी फ़ाइलें यहाँ दिखेंगी",
        "Scan a document or choose files from your phone." to "दस्तावेज़ स्कैन करें या फ़ोन से फ़ाइलें चुनें।",
        "Connect to a shop" to "शॉप से जुड़ें", "Scan the Shop QR to send documents to this shop." to "इस शॉप पर दस्तावेज़ भेजने के लिए शॉप QR स्कैन करें।",
        "Scan Shop QR" to "शॉप QR स्कैन करें", "Check Wi-Fi" to "वाई-फ़ाई जाँचें", "Open My Files" to "मेरी फ़ाइलें खोलें",
        "Already on the shop Wi-Fi? Check that your phone can reach the shop." to "क्या आप शॉप के वाई-फ़ाई पर हैं? जाँचें कि आपका फ़ोन शॉप से जुड़ पा रहा है।",
        "Cancel" to "रद्द करें", "Close" to "बंद करें", "Try again" to "फिर कोशिश करें",
        "Opening Photo Studio…" to "फोटो स्टूडियो खुल रहा है…", "Photo Studio couldn’t open" to "फोटो स्टूडियो नहीं खुल सका",
        "Export" to "एक्सपोर्ट", "Import" to "इम्पोर्ट", "Delete" to "डिलीट", "Rename" to "नाम बदलें",
        "Select all" to "सभी चुनें", "Search files" to "फ़ाइलें खोजें", "No files found" to "कोई फ़ाइल नहीं मिली",
        "Save" to "सेव करें", "Continue" to "जारी रखें", "Done" to "हो गया",
        "Camera" to "कैमरा", "Gallery" to "गैलरी", "Documents" to "दस्तावेज़",
        "Connect to shop Wi-Fi" to "शॉप के वाई-फ़ाई से जुड़ें",
        "Payment QR saved to your device." to "पेमेंट QR आपके डिवाइस में सेव हो गया।",
        "Could not share this document." to "यह दस्तावेज़ शेयर नहीं हो सका।",
        "No app is available to open this link." to "इस लिंक को खोलने के लिए कोई ऐप उपलब्ध नहीं है।"
    )

    private val hinglishCopy = mapOf(
        "Language" to "Language", "Choose the language for customer-facing app screens." to "Customer app screens ki language choose karein.",
        "My Files" to "Meri Files", "Print at a shop" to "Shop par print karein",
        "Home" to "Home", "Files" to "Files", "Print" to "Print", "Activity" to "Activity",
        "Account" to "Account", "Back" to "Back", "Back to Home" to "Home par jaayein",
        "Appearance" to "Look & Feel", "Choose how SmartPrint looks on this device. This setting is kept when you sign out." to "Is device par SmartPrint ka look choose karein. Sign out ke baad bhi setting save rahegi.",
        "Light theme" to "Light theme", "Dark theme" to "Dark theme",
        "Sign in" to "Sign in", "Sign out" to "Sign out", "Sign out?" to "Sign out karein?",
        "Create account" to "Account banayein", "Name" to "Naam", "Email address" to "Email address",
        "Password" to "Password", "Password · 12+ chars" to "Password · 12+ chars",
        "Complete the required fields." to "Please zaroori details fill karein.",
        "Print history" to "Print history", "Local files stay on this phone." to "Aapki local files isi phone mein rahengi.",
        "Guest access is ready" to "Guest access ready hai", "Smart Print customer" to "Smart Print customer",
        "Your next print starts here." to "Aapki next print yahin se start hoti hai.",
        "Everything ready for your next print." to "Aapki next print ke liye sab ready hai.",
        "Your files stay private. Print when ready." to "Aapki files private rahengi. Ready hone par print karein.",
        "Find a print shop" to "Print shop dhoondhein", "Scan the shop QR to connect" to "Connect karne ke liye shop QR scan karein",
        "Connected · tap to manage" to "Connected · manage karne ke liye tap karein",
        "ID Studio" to "ID Studio", "Photo Studio" to "Photo Studio",
        "Your space" to "Aapki space", "Recent files" to "Recent files", "View library  ›" to "Library dekhein  ›",
        "Scan document" to "Document scan karein", "Choose files" to "Files choose karein",
        "Your files will appear here" to "Aapki files yahan dikhenge",
        "Scan a document or choose files from your phone." to "Document scan karein ya phone se files choose karein.",
        "Connect to a shop" to "Shop se connect karein", "Scan the Shop QR to send documents to this shop." to "Is shop par documents bhejne ke liye Shop QR scan karein.",
        "Scan Shop QR" to "Shop QR scan karein", "Check Wi-Fi" to "Wi-Fi check karein", "Open My Files" to "My Files kholein",
        "Already on the shop Wi-Fi? Check that your phone can reach the shop." to "Shop Wi-Fi par hain? Check karein ki phone shop se connect ho raha hai.",
        "Cancel" to "Cancel", "Close" to "Close", "Try again" to "Dobara try karein",
        "Opening Photo Studio…" to "Photo Studio open ho raha hai…", "Photo Studio couldn’t open" to "Photo Studio open nahi ho saka",
        "Export" to "Export", "Import" to "Import", "Delete" to "Delete", "Rename" to "Rename",
        "Select all" to "Sab select karein", "Search files" to "Files search karein", "No files found" to "Koi file nahi mili",
        "Save" to "Save karein", "Continue" to "Continue", "Done" to "Done",
        "Camera" to "Camera", "Gallery" to "Gallery", "Documents" to "Documents",
        "Connect to shop Wi-Fi" to "Shop Wi-Fi se connect karein",
        "Payment QR saved to your device." to "Payment QR aapke device mein save ho gaya.",
        "Could not share this document." to "Ye document share nahi ho saka.",
        "No app is available to open this link." to "Is link ko open karne ke liye app available nahi hai."
    )
}
