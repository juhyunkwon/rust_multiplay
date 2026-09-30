using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace X0.MultiPlay
{
    public static class ClientUiLayout
    {
        static readonly Color PanelBg = new(0.20f, 0.22f, 0.26f, 0.97f);
        static readonly Color ButtonBg = new(0.28f, 0.46f, 0.76f, 1f);
        static readonly Color InputBg = new(0.10f, 0.11f, 0.14f, 1f);
        static readonly Color TextCol = new(0.93f, 0.94f, 0.96f, 1f);
        static readonly Color Muted = new(0.70f, 0.72f, 0.76f, 1f);
        static readonly Color SyncBg = new(0.78f, 0.80f, 0.84f, 1f);

        public static void Build(ClientHud hud)
        {
            EnsureEventSystem();

            var canvasGo = new GameObject("ClientCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(hud.transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 800);
            scaler.matchWidthOrHeight = 0.5f;

            var root = canvasGo.GetComponent<RectTransform>();
            Stretch(root);

            var left = Panel(root, "Left", new Vector2(16, -16), new Vector2(428, 496), new Color(0.14f, 0.15f, 0.18f, 0.55f));
            BuildStep1(left, hud);
            BuildStep2(left, hud);
            BuildStep3(left, hud);
            BuildSync(left, hud);

            var info = Panel(root, "Info", new Vector2(460, -16), new Vector2(400, 496), PanelBg);
            Label(info, "연결 정보", new Vector2(12, -6), new Vector2(376, 22), 15, true);
            hud.noticeBg = Image(info, "Notice", new Vector2(8, -30), new Vector2(384, 44), InputBg);
            hud.noticeText = Label(hud.noticeBg.transform, "알림: 버튼을 누르면 결과가 여기에 표시됩니다.", new Vector2(8, -2), new Vector2(368, 40), 12);
            hud.infoText = Label(info, "연결 정보 대기", new Vector2(12, -80), new Vector2(376, 408), 12);
            hud.infoText.alignment = TextAnchor.UpperLeft;
            hud.infoText.verticalOverflow = VerticalWrapMode.Truncate;

            BuildP2P(root, hud);

            Label(root, "이벤트 로그", new Vector2(16, -520), new Vector2(200, 22), 14, true);
            hud.clearLogButton = Btn(root, "로그 지우기", new Vector2(220, -518), new Vector2(100, 24));
            var logPanel = Panel(root, "Log", new Vector2(16, -546), new Vector2(844, 238), InputBg);
            hud.logText = Label(logPanel, "", new Vector2(8, -6), new Vector2(828, 226), 12);
            hud.logText.alignment = TextAnchor.UpperLeft;
            hud.logText.verticalOverflow = VerticalWrapMode.Truncate;
        }

        static void BuildStep1(RectTransform parent, ClientHud hud)
        {
            var p = Panel(parent, "Step1", new Vector2(8, -8), new Vector2(412, 86), PanelBg);
            Label(p, "Step 1. Dedicated 서버 연결 (TCP)", new Vector2(8, -4), new Vector2(396, 20), 13, true);
            Label(p, "주소:", new Vector2(8, -28), new Vector2(44, 22), 13);
            hud.dedicatedInput = Input(p, "127.0.0.1:19060", new Vector2(54, -26), new Vector2(148, 24));
            hud.connectButton = Btn(p, "연결", new Vector2(210, -24), new Vector2(88, 26));
            hud.disconnectButton = Btn(p, "연결 해제", new Vector2(304, -24), new Vector2(96, 26));
            hud.step1Status = Label(p, "상태: 대기", new Vector2(8, -56), new Vector2(196, 22), 12);
            hud.step1Status.color = Muted;
            hud.dedicatedPing = Label(p, "게임서버: ???ms", new Vector2(210, -56), new Vector2(100, 22), 12);
            hud.dedicatedPing.color = Muted;
            hud.signalingPing = Label(p, "시그널링: ???ms", new Vector2(304, -56), new Vector2(100, 22), 12);
            hud.signalingPing.color = Muted;
        }

        static void BuildStep2(RectTransform parent, ClientHud hud)
        {
            var p = Panel(parent, "Step2", new Vector2(8, -102), new Vector2(412, 92), PanelBg);
            Label(p, "Step 2. 로그인 (DB 인증)", new Vector2(8, -4), new Vector2(396, 20), 13, true);
            Label(p, "아이디:", new Vector2(8, -28), new Vector2(56, 22), 13);
            hud.userIdInput = Input(p, "", new Vector2(68, -26), new Vector2(110, 24));
            Label(p, "비밀번호:", new Vector2(8, -56), new Vector2(56, 22), 13);
            hud.passwordInput = Input(p, "", new Vector2(68, -54), new Vector2(110, 24));
            hud.passwordInput.contentType = InputField.ContentType.Password;
            hud.loginButton = Btn(p, "로그인", new Vector2(190, -26), new Vector2(70, 26));
            hud.accountAButton = Btn(p, "A", new Vector2(264, -26), new Vector2(28, 26));
            hud.accountBButton = Btn(p, "B", new Vector2(294, -26), new Vector2(28, 26));
            hud.logoutButton = Btn(p, "로그아웃", new Vector2(326, -26), new Vector2(74, 26));
            hud.step2Status = Label(p, "상태: Step 1 완료 후 가능", new Vector2(8, -72), new Vector2(390, 18), 12);
            hud.step2Status.color = Muted;
        }

        static void BuildStep3(RectTransform parent, ClientHud hud)
        {
            var p = Panel(parent, "Step3", new Vector2(8, -202), new Vector2(412, 160), PanelBg);
            Label(p, "Step 3. 룸 생성/참가 (TCP)", new Vector2(8, -4), new Vector2(396, 20), 13, true);
            hud.roomContent = ScrollList(p, "Rooms", new Vector2(8, -26), new Vector2(396, 52));
            hud.refreshRoomsButton = Btn(p, "목록 새로고침", new Vector2(8, -82), new Vector2(110, 24));
            hud.roomNameInput = Input(p, "새 룸", new Vector2(8, -110), new Vector2(120, 24));
            hud.createRoomButton = Btn(p, "방 만들기", new Vector2(132, -110), new Vector2(90, 24));
            hud.joinRoomIdInput = Input(p, "", new Vector2(8, -136), new Vector2(120, 24));
            hud.joinRoomButton = Btn(p, "참가", new Vector2(132, -136), new Vector2(90, 24));
            hud.step3Status = Label(p, "상태: Step 2 완료 후", new Vector2(230, -110), new Vector2(170, 48), 12);
            hud.step3Status.color = Muted;
        }

        static void BuildP2P(RectTransform root, ClientHud hud)
        {
            var p = Panel(root, "P2P", new Vector2(872, -16), new Vector2(392, 768), PanelBg);
            Label(p, "Step 4. P2P / 채팅 (시그널링 룸)", new Vector2(8, -6), new Vector2(376, 20), 14, true);
            hud.commStatus = Label(p, "P2P: 미접속 (방에 들어가면 자동 접속)", new Vector2(8, -28), new Vector2(376, 40), 12);
            hud.commStatus.alignment = TextAnchor.UpperLeft;
            hud.connectP2PButton = Btn(p, "P2P 접속", new Vector2(8, -72), new Vector2(120, 24));
            hud.disconnectP2PButton = Btn(p, "P2P 끊기", new Vector2(134, -72), new Vector2(120, 24));
            hud.refreshPeersButton = Btn(p, "피어 목록", new Vector2(260, -72), new Vector2(124, 24));

            var peerTitle = Label(p, "룸 피어  ·  클릭 = 귓속말 상대로 지정", new Vector2(8, -102), new Vector2(376, 18), 12);
            peerTitle.color = Muted;
            hud.peerContent = ScrollList(p, "Peers", new Vector2(8, -122), new Vector2(376, 176));

            Label(p, "받는 사람", new Vector2(8, -306), new Vector2(64, 24), 12, true);
            hud.recipientDropdown = Dropdown(p, new Vector2(74, -304), new Vector2(230, 26));
            hud.allRecipientButton = Btn(p, "전체로", new Vector2(310, -304), new Vector2(74, 26));

            var chatTitle = Label(p, "채팅  ·  받은 줄 클릭 = 그 사람에게 답장", new Vector2(8, -338), new Vector2(376, 18), 12);
            chatTitle.color = Muted;
            hud.chatContent = ScrollList(p, "Chat", new Vector2(8, -358), new Vector2(376, 334));

            hud.chatInput = Input(p, "", new Vector2(8, -700), new Vector2(300, 28));
            hud.chatPlaceholder = Label(hud.chatInput.transform, "메시지 입력 후 Enter → 전체에게", new Vector2(6, -2), new Vector2(288, 24), 12);
            hud.chatPlaceholder.color = new Color(0.55f, 0.57f, 0.62f, 1f);
            hud.chatPlaceholder.fontStyle = FontStyle.Italic;
            hud.chatInput.placeholder = hud.chatPlaceholder;
            hud.sendChatButton = Btn(p, "보내기", new Vector2(314, -700), new Vector2(70, 28));
            var hint = Label(p, "Enter = 보내기  ·  Esc = 받는 사람을 전체로", new Vector2(8, -734), new Vector2(376, 22), 11);
            hint.color = Muted;
        }

        static Dropdown Dropdown(Transform parent, Vector2 pos, Vector2 size)
        {
            var go = DefaultControls.CreateDropdown(new DefaultControls.Resources());
            go.transform.SetParent(parent, false);
            TopLeft(go.GetComponent<RectTransform>(), pos, size);
            var dd = go.GetComponent<Dropdown>();
            go.GetComponent<Image>().color = InputBg;
            foreach (var t in go.GetComponentsInChildren<Text>(true))
            {
                t.font = UiFont();
                t.fontSize = 12;
                t.color = TextCol;
                t.alignment = TextAnchor.MiddleLeft;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                t.horizontalOverflow = HorizontalWrapMode.Overflow;
            }
            foreach (var img in go.GetComponentsInChildren<Image>(true))
            {
                if (img.gameObject == go) continue;
                switch (img.name)
                {
                    case "Arrow":
                        img.color = Muted;
                        img.rectTransform.sizeDelta = new Vector2(10, 10);
                        break;
                    case "Template":
                    case "Viewport":
                        img.color = PanelBg;
                        break;
                    case "Item Background":
                        img.color = InputBg;
                        break;
                    case "Item Checkmark":
                        img.color = ButtonBg;
                        img.rectTransform.sizeDelta = new Vector2(8, 8);
                        break;
                    default:
                        img.color = new Color(0.35f, 0.37f, 0.42f, 1f);
                        break;
                }
            }
            var item = go.GetComponentInChildren<Toggle>(true);
            if (item != null)
            {
                var c = item.colors;
                c.normalColor = InputBg;
                c.highlightedColor = ButtonBg;
                c.selectedColor = ButtonBg;
                item.colors = c;
            }
            dd.ClearOptions();
            return dd;
        }

        static void BuildSync(RectTransform parent, ClientHud hud)
        {
            var p = Panel(parent, "Sync", new Vector2(8, -370), new Vector2(412, 116), PanelBg);
            Label(p, "오브젝트 동기화 (박스 드래그)", new Vector2(8, -4), new Vector2(396, 18), 13, true);
            var areaImg = Image(p, "SyncArea", new Vector2(8, -24), new Vector2(396, 64), SyncBg);
            hud.syncArea = areaImg.rectTransform;
            var box = Image(hud.syncArea, "LocalBox", new Vector2(8, -8), new Vector2(28, 28), new Color(0.12f, 0.22f, 0.78f, 1f));
            hud.localBox = box.rectTransform;
            var drag = hud.localBox.gameObject.AddComponent<SyncBoxDrag>();
            drag.Area = hud.syncArea;
            hud.syncDrag = drag;
            hud.syncAutoToggle = Toggle(p, "자동 (양쪽 모두 켜고, 상대는 주황 박스)", new Vector2(8, -92), new Vector2(396, 20));
        }

        static void EnsureEventSystem()
        {
            if (Object.FindAnyObjectByType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<StandaloneInputModule>();
        }

        static RectTransform Panel(Transform parent, string name, Vector2 pos, Vector2 size, Color color) =>
            Image(parent, name, pos, size, color).rectTransform;

        static Image Image(Transform parent, string name, Vector2 pos, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = TopLeft(go.GetComponent<RectTransform>(), pos, size);
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = true;
            _ = rt;
            return img;
        }

        static Text Label(Transform parent, string text, Vector2 pos, Vector2 size, int fontSize, bool bold = false)
        {
            var go = new GameObject(text == "" ? "Label" : text, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            TopLeft(go.GetComponent<RectTransform>(), pos, size);
            var t = go.GetComponent<Text>();
            t.font = UiFont();
            t.text = text;
            t.fontSize = fontSize;
            t.color = TextCol;
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            if (bold) t.fontStyle = FontStyle.Bold;
            return t;
        }

        static InputField Input(Transform parent, string value, Vector2 pos, Vector2 size)
        {
            var img = Image(parent, "Input", pos, size, InputBg);
            var field = img.gameObject.AddComponent<InputField>();
            var text = Label(img.transform, "", new Vector2(6, -2), new Vector2(size.x - 12, size.y - 4), 13);
            text.supportRichText = false;
            text.raycastTarget = false;
            field.textComponent = text;
            field.text = value;
            field.caretColor = Color.white;
            field.selectionColor = new Color(0.3f, 0.5f, 0.8f, 0.4f);
            return field;
        }

        static Button Btn(Transform parent, string caption, Vector2 pos, Vector2 size)
        {
            var img = Image(parent, caption, pos, size, ButtonBg);
            var btn = img.gameObject.AddComponent<Button>();
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.38f, 0.56f, 0.86f, 1f);
            colors.pressedColor = new Color(0.20f, 0.34f, 0.58f, 1f);
            colors.disabledColor = new Color(0.35f, 0.37f, 0.40f, 0.7f);
            btn.colors = colors;
            var t = Label(img.transform, caption, Vector2.zero, size, 13);
            t.alignment = TextAnchor.MiddleCenter;
            Stretch(t.rectTransform);
            return btn;
        }

        static Toggle Toggle(Transform parent, string caption, Vector2 pos, Vector2 size)
        {
            var go = new GameObject("Toggle", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            TopLeft(go.GetComponent<RectTransform>(), pos, size);
            var box = Image(go.transform, "Box", new Vector2(0, -2), new Vector2(16, 16), InputBg);
            var mark = Image(box.transform, "Check", new Vector2(2, -2), new Vector2(12, 12), ButtonBg);
            var toggle = go.AddComponent<Toggle>();
            toggle.targetGraphic = box;
            toggle.graphic = mark;
            toggle.isOn = false;
            Label(go.transform, caption, new Vector2(22, 0), new Vector2(size.x - 22, size.y), 12);
            return toggle;
        }

        static RectTransform ScrollList(Transform parent, string name, Vector2 pos, Vector2 size)
        {
            var viewport = Image(parent, name, pos, size, InputBg);
            var mask = viewport.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = true;
            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(viewport.transform, false);
            var crt = content.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0, 1);
            crt.anchorMax = new Vector2(1, 1);
            crt.pivot = new Vector2(0.5f, 1);
            crt.anchoredPosition = Vector2.zero;
            crt.sizeDelta = new Vector2(0, 0);
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            layout.spacing = 2;
            layout.padding = new RectOffset(4, 4, 2, 2);
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = crt;
            scroll.viewport = viewport.rectTransform;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            return crt;
        }

        static RectTransform TopLeft(RectTransform rt, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static Font UiFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return font != null ? font : Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
    }

    public class SyncBoxDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public RectTransform Area;
        public System.Action<int, int> Moved;
        public System.Action<int, int> Released;
        Vector2 _grab;

        public void OnBeginDrag(PointerEventData eventData)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(transform as RectTransform, eventData.position, eventData.pressEventCamera, out _grab);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Area == null) return;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(Area, eventData.position, eventData.pressEventCamera, out var local))
                return;
            var rt = (RectTransform)transform;
            var x = local.x - _grab.x;
            var y = local.y - _grab.y;
            var maxX = Area.rect.width - rt.rect.width;
            var minY = -Area.rect.height + rt.rect.height;
            x = Mathf.Clamp(x, 0, Mathf.Max(0, maxX));
            y = Mathf.Clamp(y, minY, 0);
            rt.anchoredPosition = new Vector2(x, y);
            Moved?.Invoke(Mathf.RoundToInt(x), Mathf.RoundToInt(-y));
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            var rt = (RectTransform)transform;
            Released?.Invoke(Mathf.RoundToInt(rt.anchoredPosition.x), Mathf.RoundToInt(-rt.anchoredPosition.y));
        }
    }
}
