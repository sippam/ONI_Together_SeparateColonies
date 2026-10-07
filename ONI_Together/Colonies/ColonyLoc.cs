using System;
using System.Collections.Generic;
using UnityEngine;

namespace ONI_Together.Colonies
{
    /// <summary>
    /// Thai support for the colonies UI (works with any Thai translation mod).
    ///  - Thai is detected from the game's language code ("th...") or, as a fallback, from Thai characters in a
    ///    translated game string, so it works whatever code the translation mod uses.
    ///  - T("English") / F("English {0}", args) return the Thai text when the game runs in Thai.
    ///  - ThaiFont: an OS font that has Thai glyphs (Leelawadee UI / Tahoma) for the IMGUI windows.
    /// </summary>
    public static class ColonyLoc
    {
        private static int _thai = -1; // -1 unknown, 0 no, 1 yes

        public static bool IsThai
        {
            get
            {
                if (_thai < 0) _thai = Detect() ? 1 : 0;
                return _thai == 1;
            }
        }

        /// <summary>Forget the cached result (language changed / localization re-initialised).</summary>
        public static void Reset() { _thai = -1; _font = null; _fontTried = false; }

        private static bool Detect()
        {
            try
            {
                string code = Localization.GetLocale()?.Code;
                if (!string.IsNullOrEmpty(code) && code.StartsWith("th", StringComparison.OrdinalIgnoreCase)) return true;
                code = Localization.GetCurrentLanguageCode();
                if (!string.IsNullOrEmpty(code) && code.StartsWith("th", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            try
            {
                // any translated game string with Thai characters (U+0E00..U+0E7F)
                string probe = (string)global::STRINGS.UI.TOOLS.DIG.NAME + (string)global::STRINGS.UI.FRONTEND.MAINMENU.RESUMEGAME;
                foreach (char ch in probe) if (ch >= '฀' && ch <= '๿') return true;
            }
            catch { }
            return false;
        }

        public static string T(string en)
        {
            if (en == null) return "";
            if (!IsThai) return en;
            return Th.TryGetValue(en, out var th) ? th : en;
        }

        public static string F(string en, params object[] args)
        {
            try { return string.Format(T(en), args); }
            catch { return string.Format(en, args); }
        }

        // ---- font ----

        private static Font _font;
        private static bool _fontTried;

        /// <summary>OS font with Thai glyphs, or null (then IMGUI keeps its default font).</summary>
        public static Font ThaiFont
        {
            get
            {
                if (_font != null || _fontTried) return _font;
                _fontTried = true;
                try
                {
                    _font = Font.CreateDynamicFontFromOSFont(new[] { "Leelawadee UI", "Leelawadee", "Tahoma", "Noto Sans Thai", "Arial Unicode MS", "Arial" }, 14);
                    if (_font != null) _font.hideFlags = HideFlags.HideAndDontSave;
                }
                catch (Exception ex) { Debug.LogWarning($"[ONIT-Colonies] Thai font: {ex.Message}"); _font = null; }
                return _font;
            }
        }

        // ---- Thai table (key = the exact English text used in code) ----

        private static readonly Dictionary<string, string> Th = new Dictionary<string, string>
        {
            // window / tabs
            ["Separate Colonies"] = "โคโลนีแยก",
            ["Shared colony mode"] = "โหมดโคโลนีร่วม",
            ["{0} colonies on this asteroid"] = "มี {0} โคโลนีบนดาวเคราะห์น้อยนี้",
            ["  ·  you: {0}"] = "  ·  คุณ: {0}",
            ["you are spectating"] = "คุณกำลังดูอยู่",
            ["My colony"] = "โคโลนีของฉัน",
            ["Start"] = "เริ่มต้น",
            ["Colonies ({0})"] = "โคโลนี ({0})",
            ["Rules"] = "กติกา",
            ["View"] = "การแสดงผล",
            ["Colonies"] = "โคโลนี",

            // banners
            ["SPECTATING"] = "กำลังดูอยู่",
            [" — you don't have a colony yet, your orders are ignored."] = " — คุณยังไม่มีโคโลนี คำสั่งของคุณจะไม่มีผล",
            ["Click here"] = "คลิกที่นี่",
            [" to start your own base."] = " เพื่อเริ่มฐานของคุณเอง",
            ["Click to place your Printing Pod here"] = "คลิกเพื่อวาง Printing Pod ตรงนี้",
            ["Can't place here:"] = "วางตรงนี้ไม่ได้:",
            ["   (Esc / right-click cancels)"] = "   (Esc / คลิกขวา เพื่อยกเลิก)",
            ["free"] = "ว่าง",
            ["cleared"] = "จะถูกเคลียร์",
            ["building stays"] = "สิ่งก่อสร้างยังอยู่",
            ["blocked"] = "วางไม่ได้",
            ["keep-out ring"] = "ระยะห้ามวาง",
            ["Base area: {0} cells — free {1}, cleared {2}"] = "พื้นที่ฐาน: {0} ช่อง — ว่าง {1}, เคลียร์ {2}",
            [", buildings kept {0}"] = ", สิ่งก่อสร้างคงอยู่ {0}",
            [", BLOCKED {0}"] = ", วางไม่ได้ {0}",

            // status
            ["Now click where your Printing Pod should go (Esc / right-click cancels)"] = "คลิกตำแหน่งที่จะวาง Printing Pod (Esc / คลิกขวา เพื่อยกเลิก)",
            ["Placement cancelled"] = "ยกเลิกการวางแล้ว",
            ["Founding..."] = "กำลังสร้างโคโลนี...",
            ["Request sent — waiting for host..."] = "ส่งคำขอแล้ว — รอโฮสต์...",
            ["Rules updated"] = "อัปเดตกติกาแล้ว",

            // mode off
            ["Separate colonies are off"] = "ปิดโคโลนีแยกอยู่",
            ["Everyone shares one colony (vanilla ONI Together). Turn it on to let every player run their own base on this asteroid."] = "ทุกคนใช้โคโลนีเดียวกัน (แบบ ONI Together ปกติ) เปิดเพื่อให้ผู้เล่นแต่ละคนมีฐานของตัวเองบนดาวเคราะห์น้อยนี้",
            ["Everyone shares one colony. The host can turn on separate colonies."] = "ทุกคนใช้โคโลนีเดียวกัน โฮสต์เปิดโคโลนีแยกได้",
            ["Enable separate colonies for this save"] = "เปิดโคโลนีแยกสำหรับเซฟนี้",

            // my colony
            ["{0} duplicants"] = "ดูปลิแคนท์ {0} ตัว",
            ["Research: {0}"] = "วิจัย: {0}",
            ["Printing Pod: {0}"] = "Printing Pod: {0}",
            ["Name"] = "ชื่อ",
            ["Rename"] = "เปลี่ยนชื่อ",
            ["New duplicants get the colony name as a prefix."] = "ดูปลิแคนท์ใหม่จะมีชื่อโคโลนีนำหน้า",
            ["What is yours alone"] = "อะไรเป็นของคุณคนเดียว",
            ["Research"] = "งานวิจัย",
            ["Printing Pod"] = "Printing Pod",
            ["Schedules"] = "ตารางเวลา",
            ["Errands"] = "งาน",
            ["Buildings & food"] = "สิ่งก่อสร้างและอาหาร",
            ["your own tech tree"] = "ต้นไม้วิจัยของคุณเอง",
            ["shared tech tree"] = "ต้นไม้วิจัยร่วมกัน",
            ["your own timer and choices"] = "เวลาและตัวเลือกของคุณเอง",
            ["shared — first to accept gets it"] = "ใช้ร่วมกัน — ใครรับก่อนได้ก่อน",
            ["your own"] = "ของคุณเอง",
            ["shared"] = "ใช้ร่วมกัน",
            ["your dupes, your orders first (see Work priority)"] = "ดูปลิแคนท์ของคุณ ทำคำสั่งของคุณก่อน (ดูลำดับงาน)",
            ["your dupes only work for you"] = "ดูปลิแคนท์ของคุณทำงานให้คุณเท่านั้น",
            ["anyone's dupes do anyone's work"] = "ดูปลิแคนท์ทุกตัวทำงานของทุกคน",
            ["only your colony uses them"] = "ใช้ได้แค่โคโลนีของคุณ",
            ["shared with everyone"] = "ใช้ร่วมกับทุกคน",

            // start
            ["Start your own colony"] = "เริ่มโคโลนีของคุณเอง",
            ["You are spectating. Found a colony to get a Printing Pod, a starting base and 3 duplicants of your own."] = "คุณกำลังดูอยู่ สร้างโคโลนีเพื่อรับ Printing Pod ฐานเริ่มต้น และดูปลิแคนท์ 3 ตัวของคุณเอง",
            ["Name your colony"] = "ตั้งชื่อโคโลนี",
            ["Choose 3 starting duplicants"] = "เลือกดูปลิแคนท์เริ่มต้น 3 ตัว",
            ["Click the map to place your Printing Pod"] = "คลิกบนแผนที่เพื่อวาง Printing Pod",
            ["At least {0} cells from other colonies. "] = "ห่างจากโคโลนีอื่นอย่างน้อย {0} ช่อง ",
            ["A preview shows the whole base; the area is cleared and you get the same starting room as the host."] = "จะมีพรีวิวทั้งฐาน พื้นที่จะถูกเคลียร์และคุณได้ห้องเริ่มต้นแบบเดียวกับโฮสต์",
            ["Needs a floor with a free 4x4 area."] = "ต้องมีพื้นและที่ว่าง 4x4",
            ["Placing… click the map (Esc cancels)"] = "กำลังวาง… คลิกบนแผนที่ (Esc ยกเลิก)",
            ["Place my colony"] = "วางโคโลนีของฉัน",
            ["Choose duplicants"] = "เลือกดูปลิแคนท์",
            ["Choose duplicants again"] = "เลือกดูปลิแคนท์ใหม่",
            ["Choose your starting Duplicants"] = "เลือกดูปลิแคนท์เริ่มต้นของคุณ",

            // colonies tab
            ["No colonies yet."] = "ยังไม่มีโคโลนี",
            ["  (you)"] = "  (คุณ)",
            ["{0}  ·  {1} duplicants  ·  colony #{2}"] = "{0}  ·  ดูปลิแคนท์ {1} ตัว  ·  โคโลนี #{2}",
            ["Researching: {0}"] = "กำลังวิจัย: {0}",

            // rules tab
            ["You are the host — changes apply to everyone right away and are saved in your mod options."] = "คุณเป็นโฮสต์ — การเปลี่ยนแปลงมีผลกับทุกคนทันที และบันทึกลงตัวเลือกม็อดของคุณ",
            ["Set by the host. Shown here so you know how this save plays."] = "โฮสต์เป็นคนตั้ง แสดงไว้ให้รู้ว่าเซฟนี้เล่นแบบไหน",
            ["Protection"] = "การป้องกัน",
            ["Territory protection"] = "ป้องกันอาณาเขต",
            ["No building or digging inside other colonies' circles."] = "สร้างหรือขุดในวงของโคโลนีอื่นไม่ได้",
            ["Building protection"] = "ป้องกันสิ่งก่อสร้าง",
            ["No orders on other colonies' buildings and duplicants."] = "สั่งการสิ่งก่อสร้างและดูปลิแคนท์ของโคโลนีอื่นไม่ได้",
            ["Cancel only your own orders"] = "ยกเลิกได้แค่คำสั่งของตัวเอง",
            ["The cancel tool leaves other colonies' orders alone."] = "เครื่องมือยกเลิกจะไม่แตะคำสั่งของโคโลนีอื่น",
            ["Work"] = "การทำงาน",
            ["Separate errands"] = "แยกงานดูปลิแคนท์",
            ["Duplicants work for their own colony."] = "ดูปลิแคนท์ทำงานให้โคโลนีของตัวเอง",
            ["Help other colonies"] = "ช่วยโคโลนีอื่น",
            ["Duplicants also do other colonies' errands, in each player's Work priority order."] = "ดูปลิแคนท์ช่วยทำงานของโคโลนีอื่นด้วย ตามลำดับงานที่แต่ละคนจัดไว้",
            ["Share buildings, machines & food"] = "แชร์สิ่งก่อสร้าง เครื่องจักร และอาหาร",
            ["Only duplicant control stays separate."] = "แยกกันแค่การสั่งดูปลิแคนท์",
            ["Shared duplicant management"] = "แชร์การจัดการดูปลิแคนท์",
            ["Anyone can change any dupe's priorities, food and skills."] = "ใครก็เปลี่ยน priority อาหาร และสกิลของดูปลิแคนท์ทุกตัวได้",
            ["Colony systems"] = "ระบบของโคโลนี",
            ["Separate research"] = "แยกงานวิจัย",
            ["Every colony has its own tech tree."] = "แต่ละโคโลนีมีต้นไม้วิจัยของตัวเอง",
            ["Separate Printing Pods"] = "แยก Printing Pod",
            ["Own timer and own choices for every colony."] = "แต่ละโคโลนีมีเวลาและตัวเลือกของตัวเอง",
            ["Separate schedules"] = "แยกตารางเวลา",
            ["Every colony manages its own schedules."] = "แต่ละโคโลนีจัดตารางเวลาของตัวเอง",
            ["Colony name as dupe prefix"] = "ใส่ชื่อโคโลนีหน้าชื่อดูปลิแคนท์",
            ["New duplicants are named \"Colony Name\"."] = "ดูปลิแคนท์ใหม่จะชื่อว่า \"ชื่อโคโลนี ชื่อดูปลิแคนท์\"",
            ["Host tools"] = "เครื่องมือโฮสต์",
            ["Turn separate colonies off"] = "ปิดโคโลนีแยก",
            ["Turn separate colonies on"] = "เปิดโคโลนีแยก",
            ["Hard sync all clients"] = "Hard sync ทุกคน",
            ["Hard sync sends your exact world to everyone (short pause). Background sync fixes small differences on its own."] = "Hard sync จะส่งโลกของคุณให้ทุกคนแบบตรงเป๊ะ (หยุดชั่วครู่) ส่วนการซิงก์เบื้องหลังจะแก้จุดต่างเล็ก ๆ ให้เอง",

            // view tab
            ["Overlays"] = "ภาพซ้อนบนแผนที่",
            ["Territory borders"] = "ขอบอาณาเขต",
            ["Dotted circle in each colony's colour (when territory protection is on)."] = "วงจุดสีประจำแต่ละโคโลนี (เมื่อเปิดป้องกันอาณาเขต)",
            ["Owner under the cursor"] = "เจ้าของตรงเคอร์เซอร์",
            ["Small box at the bottom-left: whose territory and building you are pointing at."] = "กล่องเล็กมุมซ้ายล่าง: บอกว่าอาณาเขตและสิ่งก่อสร้างที่ชี้อยู่เป็นของใคร",
            ["Keys"] = "ปุ่มลัด",
            ["open / close this window"] = "เปิด / ปิดหน้าต่างนี้",
            ["cancel placing a colony"] = "ยกเลิกการวางโคโลนี",

            // work priority
            ["Work priority"] = "ลำดับงาน",
            ["Reset"] = "รีเซ็ต",
            ["Your duplicants finish the errands of the colony at the top first, then move down the list."] = "ดูปลิแคนท์ของคุณจะทำงานของโคโลนีบนสุดให้เสร็จก่อน แล้วค่อยไล่ลงมาตามลำดับ",
            ["Not active: the host has to turn on \"Separate errands\" and \"Help other colonies\"."] = "ยังไม่ทำงาน: โฮสต์ต้องเปิด \"แยกงานดูปลิแคนท์\" และ \"ช่วยโคโลนีอื่น\"",
            ["  you"] = "  คุณ",
            ["your orders + unowned work"] = "คำสั่งของคุณ + งานที่ไม่มีเจ้าของ",
            ["Eating, sleeping and other personal needs always come first. A duplicant finishes its current errand before switching."] = "การกิน การนอน และความต้องการส่วนตัวอื่น ๆ มาก่อนเสมอ ดูปลิแคนท์จะทำงานที่ทำอยู่ให้เสร็จก่อนค่อยเปลี่ยน",

            // starter picker fallback
            ["Any"] = "ไม่กำหนด",
            ["Standard"] = "ปกติ",
            ["Bionic"] = "ไบโอนิก",
            ["  Bionic"] = "  ไบโอนิก",
            ["Reroll"] = "สุ่มใหม่",
            ["Reroll all"] = "สุ่มใหม่ทั้งหมด",
            ["Interests: "] = "ความสนใจ: ",
            ["Traits: "] = "นิสัย: ",
            ["Attributes: "] = "ค่าสถานะ: ",
            ["Stress: "] = "ตอนเครียด: ",
            [" · Overjoyed: "] = " · ตอนดีใจสุด ๆ: ",

            // hover owner
            ["Territory: {0}"] = "อาณาเขต: {0}",
            ["{0}: unowned"] = "{0}: ไม่มีเจ้าของ",

            // placement reasons / messages
            ["Separate colonies mode is off"] = "ปิดโหมดโคโลนีแยกอยู่",
            ["You already have a colony"] = "คุณมีโคโลนีอยู่แล้ว",
            ["Invalid cell"] = "ตำแหน่งไม่ถูกต้อง",
            ["Can't place a Printing Pod here: {0}"] = "วาง Printing Pod ตรงนี้ไม่ได้: {0}",
            ["Too close to {0} (need {1} cells)"] = "ใกล้ {0} เกินไป (ต้องห่าง {1} ช่อง)",
            ["A duplicant is standing there"] = "มีดูปลิแคนท์ยืนอยู่ตรงนั้น",
            ["Too close to the edge of the asteroid"] = "ใกล้ขอบดาวเคราะห์น้อยเกินไป",
            ["Neutronium in the way"] = "มี Neutronium ขวางอยู่",
            ["Another Printing Pod is in the way"] = "มี Printing Pod อื่นขวางอยู่",
            ["A geyser is in the way"] = "มีน้ำพุร้อนขวางอยู่",
            ["Would overwrite {0}'s buildings"] = "จะทับสิ่งก่อสร้างของ {0}",
            ["another colony"] = "โคโลนีอื่น",
            ["Host hasn't enabled separate colonies"] = "โฮสต์ยังไม่ได้เปิดโคโลนีแยก",
            ["Can't found colony: {0}"] = "สร้างโคโลนีไม่ได้: {0}",
            ["Building '{0}'... (the game must be unpaused for this)"] = "กำลังสร้าง '{0}'... (ต้องไม่หยุดเกมไว้)",
            ["Colony '{0}' founded — syncing the world..."] = "สร้างโคโลนี '{0}' แล้ว — กำลังซิงก์โลก...",
            ["Colony renamed to '{0}'"] = "เปลี่ยนชื่อโคโลนีเป็น '{0}' แล้ว",
            ["printables ready"] = "พร้อมพิมพ์แล้ว",
            ["next print in {0}s"] = "พิมพ์ครั้งถัดไปใน {0} วินาที",
        };
    }
}
