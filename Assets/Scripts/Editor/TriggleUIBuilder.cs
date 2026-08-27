using System.Collections.Generic;
using Triggle.Core;
using Triggle.Net;
using Triggle.Gameplay;
using Triggle.UI;
using Triggle.Visuals;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Triggle.EditorTools
{
    /// <summary>
    /// Builds every UI screen in the neon style: root menu, lobby, how-to-play, settings, in-game HUD,
    /// round summary and the match result panel.
    /// </summary>
    /// <remarks>
    /// The neon look is composed from three generated 9-slice sprites per control rather than a custom
    /// shader: a soft outer <b>glow</b>, then two <b>outline</b> copies offset in opposite directions and
    /// tinted cyan and coral, then a translucent <b>fill</b> on top. That two-tone rim reads as a
    /// rim-lit glass pill and costs nothing but a few Images.
    /// </remarks>
    internal static class TriggleUIBuilder
    {
        // --- type scale -----------------------------------------------------
        private const float TitleSize = 132f;
        private const float H1 = 54f;
        private const float H2 = 34f;
        private const float BodySize = 24f;
        private const float SmallSize = 19f;

        // --- palette --------------------------------------------------------
        private static readonly Color Ink = new Color(0.95f, 0.97f, 1.00f);
        private static readonly Color InkDim = new Color(0.62f, 0.68f, 0.78f);
        private static readonly Color InkFaint = new Color(0.40f, 0.45f, 0.55f);

        private static readonly Color Cyan = new Color(0.24f, 0.93f, 0.90f);
        private static readonly Color Coral = new Color(0.98f, 0.45f, 0.42f);
        private static readonly Color Green = new Color(0.30f, 0.90f, 0.45f);
        private static readonly Color Gold = new Color(1.00f, 0.80f, 0.28f);

        private static readonly Color GlassFill = new Color(0.62f, 0.68f, 0.80f, 0.13f);
        private static readonly Color CardFill = new Color(0.075f, 0.086f, 0.125f, 0.96f);
        private static readonly Color SurfaceFill = new Color(0.14f, 0.16f, 0.21f, 0.95f);
        private static readonly Color Scrim = new Color(0.020f, 0.024f, 0.039f, 0.90f);

        // --- generated sprites ----------------------------------------------
        private static Sprite _pillFill, _pillOutline, _pillGlow;
        private static Sprite _panelFill, _panelOutline, _panelGlow;
        private static Sprite _circleFill, _circleOutline;
        private static Sprite _gradient;
        private static readonly Sprite[] Avatars = new Sprite[TriggleUISprites.AvatarCount];

        /// <summary>How far a control's glow extends beyond its rect. Layout spacing must exceed 2x this.</summary>
        private const float GlowInset = 10f;

        private static TMP_FontAsset _display, _heading, _body, _bodyLight;

        // Frosted-glass materials. Null when the shader is missing, in which case panels fall back to
        // flat translucent fills and everything still renders.
        private static Material _glassPanel, _glassControl, _glassBackdrop;

        /// <summary>Everything the scene builder needs to hand over, and gets back nothing but wiring.</summary>
        internal sealed class Context
        {
            public GameFlowController Flow;
            public MatchController Match;
            public NetworkMatch Net;
            public UgsRoomService Rooms;
            public PlayerColorPalette Palette;
            public BoardThemeLibrary Themes;
            public Sprite Gradient;
            public TMP_FontAsset Display, Heading, Body, BodyLight;
        }

        // ==================================================================== entry

        internal static void Build(Context context)
        {
            _display = context.Display;
            _heading = context.Heading;
            _body = context.Body;
            _bodyLight = context.BodyLight;
            _gradient = context.Gradient;

            LoadSprites();

            var eventSystem = new GameObject("EventSystem");
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<StandaloneInputModule>();

            TriggleGlassSetup.EnableOpaqueTexture();
            bool glass = TriggleGlassSetup.TryCreateMaterials(out _glassPanel, out _glassControl,
                                                               out _glassBackdrop);

            var canvasGo = new GameObject("UI");
            var canvas = canvasGo.AddComponent<Canvas>();

            // Screen Space - Overlay is composited outside the camera render loop, so a shader on it
            // cannot sample _CameraOpaqueTexture. Screen Space - Camera can - but that mode renders
            // NOTHING without a worldCamera, so fall back to Overlay rather than risk a blank UI.
            Camera uiCamera = Camera.main;

            if (glass && uiCamera != null)
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = uiCamera;
                canvas.planeDistance = 1.2f;
            }
            else
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;

                if (glass)
                    Debug.LogWarning("[Triggle] No main camera found, so the UI canvas stays in " +
                                     "Screen Space - Overlay. Frosted glass will not render; panels " +
                                     "use flat translucent fills instead.");
            }

            canvas.sortingOrder = 100;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;

            // Match height, not a 0.5 blend. The game is landscape-only, so height is the constrained
            // axis on every device it runs on: matching it makes the authored 1080-tall design fill the
            // screen exactly, from a 4:3 monitor to a 21:9 phone, with the surplus appearing as width.
            //
            // The old 0.5 blend was only correct at exactly 16:9. On a 2340x1080 phone it produced a
            // 978-unit-tall canvas, so a 940-tall card had nowhere to go and the tall panels were
            // clipped top and bottom.
            scaler.matchWidthOrHeight = 1f;

            canvasGo.AddComponent<GraphicRaycaster>();

            // All controllers live on the canvas root so they keep running while their panel is hidden.
            var menu = canvasGo.AddComponent<MainMenuController>();
            var lobby = canvasGo.AddComponent<LobbyController>();
            var settings = canvasGo.AddComponent<SettingsPanelController>();
            var hudController = canvasGo.AddComponent<GameUIController>();
            var pause = canvasGo.AddComponent<PausePanelController>();
            var chat = canvasGo.AddComponent<ChatPanelController>();
            var multiplayer = canvasGo.AddComponent<MultiplayerPanelController>();

            RectTransform root = canvasGo.GetComponent<RectTransform>();

            // Sibling order is draw order, back to front.
            Hud hud = BuildHud(root, context.Palette);
            RoundPanel round = BuildRoundPanel(root);
            MatchPanel matchPanel = BuildMatchPanel(root);
            RootMenu rootMenu = BuildRootMenu(root);
            Lobby lobbyRefs = BuildLobby(root, context.Palette);
            HowToPlay howTo = BuildHowToPlay(root);
            MultiplayerScreen multiplayerRefs = BuildMultiplayer(root, context.Palette);
            PausePanel pauseRefs = BuildPausePanel(root);
            SettingsScreen settingsRefs = BuildSettings(root);

            WireMenu(menu, context, lobby, settings, multiplayer, rootMenu, lobbyRefs, howTo,
                     multiplayerRefs, hud);
            WireLobby(lobby, context, menu, lobbyRefs);
            WireSettings(settings, context, settingsRefs);
            WireHud(hudController, context, menu, hud, round, matchPanel);
            // The HUD pause button is wired through PausePanelController's serialized field, so
            // the listener persists in the saved scene (an AddListener call here would not).
            WirePause(pause, context, menu, settings, pauseRefs, hud.MenuButton.Button);
            WireChat(chat, context, hud.Chat);
            WireMultiplayer(multiplayer, context, menu, multiplayerRefs);

            // Saved-scene state: root menu visible, everything else hidden.
            SetHidden(hud.Group);
            SetHidden(round.Group);
            SetHidden(matchPanel.Group);
            SetHidden(lobbyRefs.Group);
            SetHidden(howTo.Group);
            SetHidden(settingsRefs.Group);
            SetHidden(pauseRefs.Group);
            SetHidden(multiplayerRefs.Group);
            SetVisible(rootMenu.Group);
        }

        private static void LoadSprites()
        {
            _pillFill = TriggleUISprites.Get(TriggleUISprites.PillFill);
            _pillOutline = TriggleUISprites.Get(TriggleUISprites.PillOutline);
            _pillGlow = TriggleUISprites.Get(TriggleUISprites.PillGlow);
            _panelFill = TriggleUISprites.Get(TriggleUISprites.PanelFill);
            _panelOutline = TriggleUISprites.Get(TriggleUISprites.PanelOutline);
            _panelGlow = TriggleUISprites.Get(TriggleUISprites.PanelGlow);
            _circleFill = TriggleUISprites.Get(TriggleUISprites.CircleFill);
            _circleOutline = TriggleUISprites.Get(TriggleUISprites.CircleOutline);

            for (int i = 0; i < Avatars.Length; i++)
                Avatars[i] = TriggleUISprites.Get(TriggleUISprites.AvatarPath(i));
        }

        // ==================================================================== neon primitives

        /// <summary>A neon control: glow behind, two offset outlines, translucent fill, optional label.</summary>
        private sealed class Neon
        {
            public RectTransform Root;
            public Image Glow;
            public Image OutlineA;
            public Image OutlineB;
            public Image Fill;
            public TMP_Text Label;
            public Button Button;
        }

        /// <summary>
        /// Composes the layered neon look. <paramref name="pill"/> picks the fully-rounded sprite set;
        /// false uses the gentler panel radius.
        /// </summary>
        private static Neon CreateNeon(RectTransform parent, string name, Vector2 anchor, Vector2 position,
                                        Vector2 size, Color fill, Color rimA, Color rimB,
                                        bool pill = true, float glowStrength = 0.5f)
        {
            var neon = new Neon { Root = CreateRect(parent, name, anchor, position, size) };

            Sprite fillSprite = pill ? _pillFill : _panelFill;
            Sprite outlineSprite = pill ? _pillOutline : _panelOutline;
            Sprite glowSprite = pill ? _pillGlow : _panelGlow;

            // Glow sits outside the control's bounds, so it is inset negatively.
            // Kept small on purpose: the glow extends outside the control's own rect, so anything
            // larger than half the layout spacing bleeds into the neighbouring control.
            neon.Glow = AddImage(neon.Root, "Glow", glowSprite,
                new Color(rimA.r, rimA.g, rimA.b, glowStrength * 0.55f), -GlowInset);

            // Two outlines offset in opposite directions give the two-tone rim light.
            neon.OutlineA = AddImage(neon.Root, "RimCyan", outlineSprite, rimA, 0f, new Vector2(-2.5f, 2.5f));
            neon.OutlineB = AddImage(neon.Root, "RimCoral", outlineSprite, rimB, 0f, new Vector2(2.5f, -2.5f));

            neon.Fill = AddImage(neon.Root, "Fill", fillSprite, fill, 2f);

            // Frosted glass replaces the flat fill where available. Cards get the heavier material so
            // text stays legible; controls get a lighter one so the board still reads through them.
            Material glass = pill ? _glassControl : _glassPanel;
            if (glass != null)
            {
                neon.Fill.material = glass;
                neon.Fill.color = Color.white;   // tint comes from the material
            }

            return neon;
        }

        /// <summary>Neon control plus a click handler, a label and hover/press feedback.</summary>
        private static Neon CreateNeonButton(RectTransform parent, string name, string label,
                                              TMP_FontAsset font, float fontSize, Vector2 anchor,
                                              Vector2 position, Vector2 size, Color rimA, Color rimB,
                                              Color textColor, Color? fill = null)
        {
            Neon neon = CreateNeon(parent, name, anchor, position, size,
                fill ?? GlassFill, rimA, rimB);

            // The fill is the click target; the glow and rims must not intercept pointer events.
            neon.Fill.raycastTarget = true;

            neon.Button = neon.Root.gameObject.AddComponent<Button>();
            neon.Button.targetGraphic = neon.Fill;

            ColorBlock colors = neon.Button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = Color.white;
            colors.pressedColor = Color.white;
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(1f, 1f, 1f, 0.45f);
            neon.Button.colors = colors;

            neon.Label = CreateText(neon.Root, "Label", font, fontSize, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0.5f), Vector2.zero, size, label, textColor);
            Stretch(neon.Label.rectTransform, 0f);

            neon.Root.gameObject.AddComponent<UIButtonFeedback>();
            return neon;
        }

        private static Image AddImage(RectTransform parent, string name, Sprite sprite, Color color,
                                       float inset, Vector2 offset = default)
        {
            RectTransform rect = CreateRect(parent, name, new Vector2(0.5f, 0.5f), offset, Vector2.zero);
            Stretch(rect, inset);
            rect.anchoredPosition = offset;

            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        // ==================================================================== root menu

        private sealed class RootMenu
        {
            public CanvasGroup Group;
            public Neon PlayLocal, PlayAi, PlayOnline, HowToPlay, Settings, Quit;
            public TMP_Text AiSubLabel;
        }

        private static RootMenu BuildRootMenu(RectTransform parent)
        {
            var refs = new RootMenu();
            RectTransform panel = CreateFullScreen(parent, "RootMenu", out refs.Group);
            AddScrim(panel, Scrim);

            // --- neon title ---------------------------------------------------
            RectTransform titleRect = CreateRect(panel, "Title", new Vector2(0.5f, 1f),
                new Vector2(0f, -250f), new Vector2(1200f, 190f));

            // Layered copies give the title its glow: coral behind, cyan offset, white core on top.
            TMP_Text glowCoral = CreateText(titleRect, "TitleGlowCoral", _display, TitleSize,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), new Vector2(-5f, -4f),
                new Vector2(1200f, 190f), "TRIGGLE", new Color(Coral.r, Coral.g, Coral.b, 0.85f));
            glowCoral.characterSpacing = 6f;

            TMP_Text glowCyan = CreateText(titleRect, "TitleGlowCyan", _display, TitleSize,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), new Vector2(5f, 4f),
                new Vector2(1200f, 190f), "TRIGGLE", new Color(Cyan.r, Cyan.g, Cyan.b, 0.85f));
            glowCyan.characterSpacing = 6f;

            TMP_Text titleCore = CreateText(titleRect, "TitleCore", _display, TitleSize,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(1200f, 190f), "TRIGGLE", Ink);
            titleCore.characterSpacing = 6f;

            CreateText(panel, "Tagline", _bodyLight, BodySize, TextAlignmentOptions.Center,
                new Vector2(0.5f, 1f), new Vector2(0f, -360f), new Vector2(900f, 32f),
                "CHAIN TRIANGLE GAME", Cyan).characterSpacing = 16f;

            // --- buttons ------------------------------------------------------
            // One vertical layout group, so the buttons cannot collide however the sizes change.
            const float buttonWidth = 470f;

            // 84 rather than 92: six rows plus the AI caption come to 542 units at this height, which
            // clears the tagline above (its lower edge is at +164) and the Quit button below. At 92 the
            // top button ran into the tagline.
            const float buttonHeight = 84f;

            // Content spans -391..+151 once laid out: clear of the tagline at +164 and of Quit at -441.
            RectTransform column = CreateColumn(panel, "MenuButtons", new Vector2(0.5f, 0.5f),
                new Vector2(0f, -120f), new Vector2(560f, 560f), 20f);

            refs.PlayLocal = CreateNeonButton(column, "PlayLocalButton", "Play Local", _heading, H2,
                Vector2.zero, Vector2.zero, new Vector2(buttonWidth, buttonHeight), Cyan, Coral, Ink);
            SetLayoutSize(refs.PlayLocal.Root, buttonWidth, buttonHeight);

            refs.PlayAi = CreateNeonButton(column, "PlayAiButton", "Play vs AI", _heading, H2,
                Vector2.zero, Vector2.zero, new Vector2(buttonWidth, buttonHeight), Cyan, Coral, Ink);
            SetLayoutSize(refs.PlayAi.Root, buttonWidth, buttonHeight);

            // Its own row in the column, not floating over the button it describes.
            refs.AiSubLabel = CreateText(column, "PlayAiSubLabel", _bodyLight, 16f,
                TextAlignmentOptions.Center, Vector2.zero, Vector2.zero,
                new Vector2(300f, 22f), "Difficulty: Normal", InkFaint);
            SetLayoutSize(refs.AiSubLabel.rectTransform, 300f, 22f);

            refs.PlayOnline = CreateNeonButton(column, "PlayOnlineButton", "Play Online", _heading, H2,
                Vector2.zero, Vector2.zero, new Vector2(buttonWidth, buttonHeight), Cyan, Coral, Ink);
            SetLayoutSize(refs.PlayOnline.Root, buttonWidth, buttonHeight);

            refs.HowToPlay = CreateNeonButton(column, "HowToPlayButton", "How to Play", _heading, H2,
                Vector2.zero, Vector2.zero, new Vector2(buttonWidth, buttonHeight), Cyan, Coral, Ink);
            SetLayoutSize(refs.HowToPlay.Root, buttonWidth, buttonHeight);

            refs.Settings = CreateNeonButton(column, "SettingsButton", "Settings", _heading, H2,
                Vector2.zero, Vector2.zero, new Vector2(buttonWidth, buttonHeight), Cyan, Coral, Ink);
            SetLayoutSize(refs.Settings.Root, buttonWidth, buttonHeight);

            refs.Quit = CreateNeonButton(panel, "QuitButton", "Quit", _body, BodySize,
                new Vector2(0.5f, 0f), new Vector2(0f, 70f), new Vector2(220f, 58f),
                InkFaint, InkFaint, InkDim);

            CreateText(panel, "Version", _bodyLight, 15f, TextAlignmentOptions.Right,
                new Vector2(1f, 0f), new Vector2(-120f, 34f), new Vector2(200f, 22f), "v1.0", InkFaint);

            return refs;
        }

        // ==================================================================== multiplayer

        private sealed class MultiplayerScreen
        {
            public CanvasGroup Group;
            public Neon Host, Join, Start, Leave, Back;
            public TMP_Text RoomCode, Status, StartLabel;
            public TMP_InputField CodeInput;
            public TMP_Text[] PlayerRows = new TMP_Text[SeatRoster.SeatCount];
        }

        private static MultiplayerScreen BuildMultiplayer(RectTransform parent, PlayerColorPalette palette)
        {
            var refs = new MultiplayerScreen();
            RectTransform panel = CreateFullScreen(parent, "MultiplayerPanel", out refs.Group);
            AddScrim(panel, Scrim);

            Neon card = CreateNeon(panel, "Card", new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(1000f, 720f), CardFill, Cyan, Coral, false, 0.35f);
            RectTransform c = card.Root;

            Neon header = CreateNeon(c, "HeaderChip", new Vector2(0.5f, 1f), new Vector2(0f, 8f),
                new Vector2(460f, 84f), SurfaceFill, Cyan, Coral);
            CreateText(header.Root, "HeaderLabel", _heading, H2, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(460f, 44f), "PLAY ONLINE", Cyan);

            // --- host, left half ----------------------------------------------
            const float column = 240f;

            CreateText(c, "HostHeader", _bodyLight, SmallSize, TextAlignmentOptions.Center,
                new Vector2(0.5f, 1f), new Vector2(-column, -128f), new Vector2(420f, 24f),
                "HOST A ROOM", InkFaint).characterSpacing = 8f;

            refs.Host = CreateNeonButton(c, "HostButton", "CREATE ROOM", _heading, 26f,
                new Vector2(0.5f, 1f), new Vector2(-column, -186f), new Vector2(380f, 74f),
                Green, Green, Color.white, new Color(Green.r, Green.g, Green.b, 0.20f));

            Neon codeBox = CreateNeon(c, "RoomCodeBox", new Vector2(0.5f, 1f),
                new Vector2(-column, -272f), new Vector2(380f, 78f), SurfaceFill, Cyan, Cyan);
            refs.RoomCode = CreateText(codeBox.Root, "RoomCode", _display, 42f,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(380f, 54f), "- - - - - -", Ink);
            refs.RoomCode.characterSpacing = 8f;

            // --- join, right half ---------------------------------------------
            CreateText(c, "JoinHeader", _bodyLight, SmallSize, TextAlignmentOptions.Center,
                new Vector2(0.5f, 1f), new Vector2(column, -128f), new Vector2(420f, 24f),
                "JOIN WITH A CODE", InkFaint).characterSpacing = 8f;

            refs.CodeInput = CreateInputField(c, "CodeInput", new Vector2(0.5f, 1f),
                new Vector2(column, -186f), new Vector2(380f, 74f), 30f, "ROOM CODE", Cyan);

            refs.Join = CreateNeonButton(c, "JoinButton", "JOIN ROOM", _heading, 26f,
                new Vector2(0.5f, 1f), new Vector2(column, -272f), new Vector2(380f, 78f),
                Cyan, Coral, Ink);

            // --- roster --------------------------------------------------------
            CreateText(c, "PlayersHeader", _bodyLight, SmallSize, TextAlignmentOptions.Center,
                new Vector2(0.5f, 1f), new Vector2(0f, -344f), new Vector2(600f, 24f),
                "IN THE ROOM", InkFaint).characterSpacing = 8f;

            RectTransform roster = CreateColumn(c, "PlayerRows", new Vector2(0.5f, 1f),
                new Vector2(0f, -430f), new Vector2(700f, 140f), 6f);

            for (int i = 0; i < SeatRoster.SeatCount; i++)
            {
                refs.PlayerRows[i] = CreateText(roster, $"PlayerRow{i}", _body, 21f,
                    TextAlignmentOptions.Center, Vector2.zero, Vector2.zero, new Vector2(700f, 28f),
                    $"Seat {i + 1} - empty", InkDim);

                refs.PlayerRows[i].overflowMode = TextOverflowModes.Ellipsis;
                SetLayoutSize(refs.PlayerRows[i].rectTransform, 700f, 28f);
            }

            refs.Status = CreateText(c, "Status", _bodyLight, 17f, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0f), new Vector2(0f, 172f), new Vector2(880f, 26f),
                "Host a room, or type a friend's code.", InkDim);

            // --- actions -------------------------------------------------------
            refs.Start = CreateNeonButton(c, "StartButton", "START GAME", _display, 34f,
                new Vector2(0.5f, 0f), new Vector2(0f, 96f), new Vector2(460f, 84f),
                Green, Green, Color.white, new Color(Green.r, Green.g, Green.b, 0.22f));
            refs.StartLabel = refs.Start.Label;

            refs.Leave = CreateNeonButton(c, "LeaveButton", "Leave room", _body, BodySize,
                new Vector2(1f, 0f), new Vector2(-150f, 44f), new Vector2(220f, 54f),
                Coral, Coral, InkDim);

            refs.Back = CreateNeonButton(c, "BackButton", "Back", _body, BodySize,
                new Vector2(0f, 0f), new Vector2(130f, 44f), new Vector2(180f, 54f),
                InkFaint, InkFaint, InkDim);

            return refs;
        }

        private static void WireMultiplayer(MultiplayerPanelController controller, Context context,
                                             MainMenuController menu, MultiplayerScreen refs)
        {
            using var so = new SerializedWiring(controller);

            so.Ref("rooms", context.Rooms);
            so.Ref("networkMatch", context.Net);
            so.Ref("flowController", context.Flow);
            so.Ref("matchController", context.Match);
            so.Ref("mainMenu", menu);
            so.Ref("palette", context.Palette);

            so.Ref("panel", refs.Group);
            so.Ref("hostButton", refs.Host.Button);
            so.Ref("roomCodeLabel", refs.RoomCode);
            so.Ref("codeInput", refs.CodeInput);
            so.Ref("joinButton", refs.Join.Button);
            so.Ref("statusLabel", refs.Status);
            so.Ref("startButton", refs.Start.Button);
            so.Ref("startLabel", refs.StartLabel);
            so.Ref("leaveButton", refs.Leave.Button);
            so.Ref("backButton", refs.Back.Button);

            so.ArraySize("playerRows", SeatRoster.SeatCount);
            for (int i = 0; i < SeatRoster.SeatCount; i++)
                so.Ref($"playerRows.Array.data[{i}]", refs.PlayerRows[i]);
        }

        // ==================================================================== lobby

        private sealed class Lobby
        {
            public CanvasGroup Group;
            public Neon[] CountButtons = new Neon[3];
            public LobbySeat[] Seats = new LobbySeat[4];
            public Neon RoundsDown, RoundsUp, Start, Back;
            public TMP_Text RoundsValue, RoundsCaption;
            public GameObject DifficultyRoot;
            public Neon DifficultyDown, DifficultyUp;
            public TMP_Text DifficultyValue, DifficultyCaption;
        }

        private sealed class LobbySeat
        {
            public GameObject Root;
            public Image Outline;
            public Image Avatar;
            public TMP_InputField NameInput;
            public Button KindButton;
            public TMP_Text KindLabel;
            public Button[] ColorButtons = new Button[PlayerProfiles.ColorSlotCount];
            public GameObject[] ColorMarkers = new GameObject[PlayerProfiles.ColorSlotCount];
        }

        private static Lobby BuildLobby(RectTransform parent, PlayerColorPalette palette)
        {
            var refs = new Lobby();
            RectTransform panel = CreateFullScreen(parent, "LobbyPanel", out refs.Group);
            AddScrim(panel, Scrim);

            Neon card = CreateNeon(panel, "Card", new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(1180f, 940f), CardFill, Cyan, Coral, false, 0.35f);
            RectTransform c = card.Root;

            // --- header chip --------------------------------------------------
            Neon header = CreateNeon(c, "HeaderChip", new Vector2(0.5f, 1f), new Vector2(0f, 8f),
                new Vector2(460f, 84f), SurfaceFill, Cyan, Coral);
            CreateText(header.Root, "HeaderLabel", _heading, H2, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(460f, 44f), "GAME LOBBY", Cyan);

            // --- player count -------------------------------------------------
            RectTransform countRow = CreateRow(c, "PlayerCountRow", new Vector2(0.5f, 1f),
                new Vector2(0f, -110f), new Vector2(800f, 78f), 30f);

            for (int i = 0; i < 3; i++)
            {
                refs.CountButtons[i] = CreateNeonButton(countRow, $"Count{i + 2}", $"{i + 2} Players",
                    _heading, 28f, Vector2.zero, Vector2.zero,
                    new Vector2(230f, 74f), Coral, Coral, Ink);
                SetLayoutSize(refs.CountButtons[i].Root, 230f, 74f);
            }

            // --- seat rows ----------------------------------------------------
            RectTransform seatColumn = CreateColumn(c, "SeatRows", new Vector2(0.5f, 1f),
                new Vector2(0f, -420f), new Vector2(1060f, 480f), 24f);

            for (int i = 0; i < 4; i++)
                refs.Seats[i] = BuildLobbySeat(seatColumn, i, palette);

            // --- rounds and difficulty steppers -------------------------------
            // Side by side rather than stacked: the seat rows already reach to within 20px of this
            // block, so a second full-width row would not fit inside the card.
            const float stepperY = 210f;
            const float headerY = 260f;
            const float captionY = 164f;
            const float groupX = 290f;

            CreateText(c, "RoundsHeader", _bodyLight, SmallSize, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0f), new Vector2(-groupX, headerY), new Vector2(520f, 24f),
                "ROUNDS", InkFaint).characterSpacing = 8f;

            refs.RoundsDown = CreateNeonButton(c, "RoundsDown", "-", _heading, H2,
                new Vector2(0.5f, 0f), new Vector2(-groupX - 140f, stepperY), new Vector2(78f, 66f),
                Cyan, Coral, Ink);

            Neon roundsBox = CreateNeon(c, "RoundsBox", new Vector2(0.5f, 0f),
                new Vector2(-groupX, stepperY), new Vector2(200f, 66f), SurfaceFill, Cyan, Cyan);
            refs.RoundsValue = CreateText(roundsBox.Root, "RoundsValue", _heading, H2,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(200f, 44f), "1", Ink);

            refs.RoundsUp = CreateNeonButton(c, "RoundsUp", "+", _heading, H2,
                new Vector2(0.5f, 0f), new Vector2(-groupX + 140f, stepperY), new Vector2(78f, 66f),
                Cyan, Coral, Ink);

            refs.RoundsCaption = CreateText(c, "RoundsCaption", _bodyLight, 16f,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0f), new Vector2(-groupX, captionY),
                new Vector2(540f, 24f), "Single round - no round counter", InkFaint);

            // The whole difficulty group is one object so LobbyController can switch it off in a
            // hot-seat game, where a computer skill level would promise something the match never uses.
            // Stretched to the card's own rect, so its children use the same coordinates as the
            // rounds group above.
            RectTransform aiGroup = CreateRect(c, "DifficultyGroup", new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero);
            Stretch(aiGroup, 0f);
            refs.DifficultyRoot = aiGroup.gameObject;

            CreateText(aiGroup, "DifficultyHeader", _bodyLight, SmallSize, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0f), new Vector2(groupX, headerY), new Vector2(520f, 24f),
                "COMPUTER", InkFaint).characterSpacing = 8f;

            refs.DifficultyDown = CreateNeonButton(aiGroup, "DifficultyDown", "-", _heading, H2,
                new Vector2(0.5f, 0f), new Vector2(groupX - 140f, stepperY), new Vector2(78f, 66f),
                Cyan, Coral, Ink);

            Neon difficultyBox = CreateNeon(aiGroup, "DifficultyBox", new Vector2(0.5f, 0f),
                new Vector2(groupX, stepperY), new Vector2(200f, 66f), SurfaceFill, Gold, Gold);
            refs.DifficultyValue = CreateText(difficultyBox.Root, "DifficultyValue", _heading, 28f,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(200f, 44f), "NORMAL", Gold);

            refs.DifficultyUp = CreateNeonButton(aiGroup, "DifficultyUp", "+", _heading, H2,
                new Vector2(0.5f, 0f), new Vector2(groupX + 140f, stepperY), new Vector2(78f, 66f),
                Cyan, Coral, Ink);

            refs.DifficultyCaption = CreateText(aiGroup, "DifficultyCaption", _bodyLight, 16f,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0f), new Vector2(groupX, captionY),
                new Vector2(540f, 24f), "Takes what is on offer and avoids the obvious gift", InkFaint);

            // --- actions ------------------------------------------------------
            refs.Start = CreateNeonButton(c, "StartButton", "START GAME", _display, 40f,
                new Vector2(0.5f, 0f), new Vector2(0f, 86f), new Vector2(520f, 96f),
                Green, Green, Color.white, new Color(Green.r, Green.g, Green.b, 0.22f));

            refs.Back = CreateNeonButton(c, "BackButton", "Back", _body, BodySize,
                new Vector2(0f, 0f), new Vector2(130f, 44f), new Vector2(180f, 54f),
                InkFaint, InkFaint, InkDim);

            return refs;
        }

        private static LobbySeat BuildLobbySeat(RectTransform parent, int index,
                                                 PlayerColorPalette palette)
        {
            var seat = new LobbySeat();
            Color color = palette.GetColorBySlot(index);

            // Positioned by the parent VerticalLayoutGroup; only the size is declared here.
            Neon row = CreateNeon(parent, $"SeatRow_{index + 1}", Vector2.zero,
                Vector2.zero, new Vector2(1040f, 96f), GlassFill, color, Coral);
            SetLayoutSize(row.Root, 1040f, 96f);

            seat.Root = row.Root.gameObject;
            seat.Outline = row.OutlineA;

            // --- avatar chip --------------------------------------------------
            Neon avatarChip = CreateNeon(row.Root, "AvatarChip", new Vector2(0f, 0.5f),
                new Vector2(66f, 0f), new Vector2(84f, 84f), SurfaceFill, color, color, false);

            RectTransform avatarRect = CreateRect(avatarChip.Root, "Avatar", new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(56f, 56f));
            var avatarImage = avatarRect.gameObject.AddComponent<Image>();
            avatarImage.sprite = Avatars[index % Avatars.Length];
            avatarImage.color = color;
            avatarImage.raycastTarget = false;
            avatarImage.preserveAspect = true;
            seat.Avatar = avatarImage;

            // --- name input ---------------------------------------------------
            seat.NameInput = CreateInputField(row.Root, "NameInput", new Vector2(0f, 0.5f),
                new Vector2(430f, 0f), new Vector2(480f, 66f), BodySize,
                palette.GetColorName(index), color);

            // --- human / CPU toggle -------------------------------------------
            // Sits in the gap between the name field (ends at 670) and the first swatch (starts at
            // 824), so it needs no extra width on an already full row.
            Neon kind = CreateNeonButton(row.Root, "KindToggle", "HUMAN", _body, 20f,
                new Vector2(0f, 0.5f), new Vector2(747f, 0f), new Vector2(134f, 54f),
                Cyan, Coral, InkDim);

            seat.KindButton = kind.Button;
            seat.KindLabel = kind.Label;

            // --- colour swatches ----------------------------------------------
            for (int slot = 0; slot < PlayerProfiles.ColorSlotCount; slot++)
            {
                float x = -190f + slot * 62f;
                Color slotColor = palette.GetColorBySlot(slot);

                RectTransform swatchRect = CreateRect(row.Root, $"Color{slot}", new Vector2(1f, 0.5f),
                    new Vector2(x, 0f), new Vector2(52f, 52f));

                var swatch = swatchRect.gameObject.AddComponent<Image>();
                swatch.sprite = _panelFill;
                swatch.type = Image.Type.Sliced;
                swatch.color = slotColor;
                swatch.raycastTarget = true;

                var button = swatchRect.gameObject.AddComponent<Button>();
                button.targetGraphic = swatch;

                ColorBlock colors = button.colors;
                colors.normalColor = Color.white;
                colors.highlightedColor = Color.white;
                colors.pressedColor = Color.white;
                colors.selectedColor = Color.white;
                button.colors = colors;

                swatchRect.gameObject.AddComponent<UIButtonFeedback>();
                seat.ColorButtons[slot] = button;

                // Selection ring, toggled by LobbyController.
                Image marker = AddImage(swatchRect, "SelectedRing", _panelOutline, Color.white, -7f);
                seat.ColorMarkers[slot] = marker.gameObject;
            }

            return seat;
        }

        // ==================================================================== how to play

        private sealed class HowToPlay
        {
            public CanvasGroup Group;
            public TMP_Text Body;
            public Neon Close;
        }

        private static HowToPlay BuildHowToPlay(RectTransform parent)
        {
            var refs = new HowToPlay();
            RectTransform panel = CreateFullScreen(parent, "HowToPlayPanel", out refs.Group);
            AddScrim(panel, Scrim);

            Neon card = CreateNeon(panel, "Card", new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(1080f, 800f), CardFill, Cyan, Coral, false, 0.35f);
            RectTransform c = card.Root;

            Neon header = CreateNeon(c, "HeaderChip", new Vector2(0.5f, 1f), new Vector2(0f, 8f),
                new Vector2(440f, 84f), SurfaceFill, Cyan, Coral);
            CreateText(header.Root, "HeaderLabel", _heading, H2, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(440f, 44f), "HOW TO PLAY", Cyan);

            refs.Body = CreateText(c, "Body", _bodyLight, BodySize, TextAlignmentOptions.TopLeft,
                new Vector2(0.5f, 0.5f), new Vector2(0f, -10f), new Vector2(920f, 520f),
                string.Empty, InkDim);
            refs.Body.enableWordWrapping = true;
            refs.Body.lineSpacing = 6f;
            refs.Body.paragraphSpacing = 14f;

            refs.Close = CreateNeonButton(c, "CloseButton", "Back", _heading, 28f,
                new Vector2(0.5f, 0f), new Vector2(0f, 68f), new Vector2(300f, 76f),
                Cyan, Coral, Ink);

            return refs;
        }

        // ==================================================================== settings

        private sealed class SettingsScreen
        {
            public CanvasGroup Group;
            public Neon Close, AudioTab, BoardTab, SizeDown, SizeUp;
            public GameObject AudioContent, BoardContent;
            public Image AudioUnderline, BoardUnderline;
            public TMP_Text AudioLabel, BoardLabel;
            public Slider Master, Music, Sfx;
            public TMP_Text MasterValue, MusicValue, SfxValue;
            public TMP_Text SizeValue, SizeCaption;
            public GameObject LockedNotice;
            public TMP_Text LockedLabel;
            public ThemeChipRefs[] Themes = new ThemeChipRefs[6];
        }

        private sealed class ThemeChipRefs
        {
            public GameObject Root;
            public Button Button;
            public Image Swatch;
            public Image Accent;
            public GameObject Marker;
            public TMP_Text Label;
        }

        private static SettingsScreen BuildSettings(RectTransform parent)
        {
            var refs = new SettingsScreen();
            RectTransform panel = CreateFullScreen(parent, "SettingsPanel", out refs.Group);
            AddScrim(panel, Scrim);

            Neon card = CreateNeon(panel, "Card", new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(1180f, 860f), CardFill, Cyan, Coral, false, 0.35f);
            RectTransform c = card.Root;

            Neon header = CreateNeon(c, "HeaderChip", new Vector2(0.5f, 1f), new Vector2(0f, 8f),
                new Vector2(400f, 84f), SurfaceFill, Cyan, Coral);
            CreateText(header.Root, "HeaderLabel", _heading, H2, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(400f, 44f), "SETTINGS", Coral);

            refs.Close = CreateNeonButton(c, "CloseButton", "X", _heading, 30f,
                new Vector2(1f, 1f), new Vector2(-64f, -70f), new Vector2(64f, 64f),
                Coral, Coral, Ink);

            // --- tabs ---------------------------------------------------------
            refs.AudioTab = CreateNeonButton(c, "AudioTab", "Audio", _heading, 28f,
                new Vector2(0.5f, 1f), new Vector2(-170f, -128f), new Vector2(280f, 70f),
                Cyan, Cyan, Cyan);
            refs.AudioLabel = refs.AudioTab.Label;

            refs.BoardTab = CreateNeonButton(c, "BoardTab", "Board", _heading, 28f,
                new Vector2(0.5f, 1f), new Vector2(170f, -128f), new Vector2(280f, 70f),
                InkFaint, InkFaint, InkFaint);
            refs.BoardLabel = refs.BoardTab.Label;

            refs.AudioUnderline = CreatePanel(c, "AudioUnderline", _pillFill, new Vector2(0.5f, 1f),
                new Vector2(-170f, -170f), new Vector2(240f, 5f), Cyan);
            refs.BoardUnderline = CreatePanel(c, "BoardUnderline", _pillFill, new Vector2(0.5f, 1f),
                new Vector2(170f, -170f), new Vector2(240f, 5f), Color.clear);

            // --- audio tab ----------------------------------------------------
            RectTransform audio = CreateRect(c, "AudioContent", new Vector2(0.5f, 0.5f),
                new Vector2(0f, -60f), new Vector2(1000f, 460f));
            refs.AudioContent = audio.gameObject;

            RectTransform sliderColumn = CreateColumn(audio, "SliderColumn", new Vector2(0.5f, 1f),
                new Vector2(0f, -172f), new Vector2(920f, 330f), 22f);

            refs.Master = CreateSliderRow(sliderColumn, "Master", "MASTER VOLUME", out refs.MasterValue);
            refs.Music = CreateSliderRow(sliderColumn, "Music", "MUSIC", out refs.MusicValue);
            refs.Sfx = CreateSliderRow(sliderColumn, "Sfx", "SOUND EFFECTS", out refs.SfxValue);

            CreateText(audio, "AudioCredit", _bodyLight, 15f, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0f), new Vector2(0f, -34f), new Vector2(940f, 40f),
                "Music: \"Loop - House In a Forest\" by HorrorPen (CC-BY 3.0).  SFX by Kenney (CC0).",
                InkFaint).enableWordWrapping = true;

            // --- board tab ----------------------------------------------------
            RectTransform board = CreateRect(c, "BoardContent", new Vector2(0.5f, 0.5f),
                new Vector2(0f, -60f), new Vector2(1000f, 460f));
            refs.BoardContent = board.gameObject;

            // The two tabs occupy the same rect. SettingsPanelController picks one at runtime, but the
            // saved scene had both switched on, so they were stacked - the theme chips sat over the
            // volume sliders until the first tab click sorted it out.
            board.gameObject.SetActive(false);

            CreateText(board, "ThemeHeader", _bodyLight, SmallSize, TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(160f, -18f), new Vector2(320f, 26f),
                "BOARD THEME", InkFaint).characterSpacing = 8f;

            // GridLayoutGroup rather than hand-placed cells, for the same reason as the columns.
            RectTransform themeGrid = CreateRect(board, "ThemeGrid", new Vector2(0.5f, 1f),
                new Vector2(0f, -178f), new Vector2(840f, 300f));

            var grid = themeGrid.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(250f, 128f);
            grid.spacing = new Vector2(26f, 24f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 3;
            grid.childAlignment = TextAnchor.UpperCenter;

            for (int i = 0; i < 6; i++) refs.Themes[i] = BuildThemeChip(themeGrid, i);

            CreateText(board, "SizeHeader", _bodyLight, SmallSize, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0f), new Vector2(0f, 96f), new Vector2(400f, 26f),
                "BOARD SIZE", InkFaint).characterSpacing = 8f;

            refs.SizeDown = CreateNeonButton(board, "SizeDown", "-", _heading, H2,
                new Vector2(0.5f, 0f), new Vector2(-140f, 44f), new Vector2(72f, 62f),
                Cyan, Coral, Ink);

            Neon sizeBox = CreateNeon(board, "SizeBox", new Vector2(0.5f, 0f), new Vector2(0f, 44f),
                new Vector2(150f, 62f), SurfaceFill, Cyan, Cyan);
            refs.SizeValue = CreateText(sizeBox.Root, "SizeValue", _heading, H2,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(150f, 44f), "3", Ink);

            refs.SizeUp = CreateNeonButton(board, "SizeUp", "+", _heading, H2,
                new Vector2(0.5f, 0f), new Vector2(140f, 44f), new Vector2(72f, 62f),
                Cyan, Coral, Ink);

            refs.SizeCaption = CreateText(board, "SizeCaption", _bodyLight, 16f,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0f), new Vector2(0f, 8f),
                new Vector2(600f, 24f), "37 pegs  -  54 triangles", InkFaint);

            // --- locked notice ------------------------------------------------
            Neon locked = CreateNeon(board, "LockedNotice", new Vector2(0.5f, 1f), new Vector2(0f, 22f),
                new Vector2(940f, 56f), new Color(Coral.r, Coral.g, Coral.b, 0.16f), Coral, Coral);
            refs.LockedNotice = locked.Root.gameObject;
            refs.LockedLabel = CreateText(locked.Root, "LockedLabel", _bodyLight, 17f,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(920f, 40f), string.Empty, Coral);

            return refs;
        }

        private static ThemeChipRefs BuildThemeChip(RectTransform parent, int index)
        {
            var chip = new ThemeChipRefs();

            // Cell position and size come from the parent GridLayoutGroup.
            RectTransform root = CreateRect(parent, $"ThemeChip_{index}", new Vector2(0.5f, 1f),
                Vector2.zero, new Vector2(250f, 128f));
            chip.Root = root.gameObject;

            var swatch = root.gameObject.AddComponent<Image>();
            swatch.sprite = _panelFill;
            swatch.type = Image.Type.Sliced;
            swatch.color = new Color(0.12f, 0.14f, 0.19f);
            swatch.raycastTarget = true;
            chip.Swatch = swatch;

            chip.Accent = AddImage(root, "Accent", _pillFill, Cyan, 0f);
            RectTransform accentRect = chip.Accent.rectTransform;
            accentRect.anchorMin = new Vector2(0.12f, 0f);
            accentRect.anchorMax = new Vector2(0.88f, 0f);
            accentRect.offsetMin = new Vector2(0f, 34f);
            accentRect.offsetMax = new Vector2(0f, 40f);

            chip.Marker = AddImage(root, "SelectedRing", _panelOutline, Cyan, -5f).gameObject;

            chip.Label = CreateText(root, "Label", _bodyLight, 17f, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0f), new Vector2(0f, 16f), new Vector2(230f, 26f), "Theme", InkDim);

            chip.Button = root.gameObject.AddComponent<Button>();
            chip.Button.targetGraphic = swatch;
            root.gameObject.AddComponent<UIButtonFeedback>();

            return chip;
        }

        /// <summary>
        /// One self-contained slider row: caption and percentage on the top line, track underneath.
        /// Sized as a layout element so the parent column spaces the rows.
        /// </summary>
        private static Slider CreateSliderRow(RectTransform column, string name, string label,
                                               out TMP_Text valueLabel)
        {
            const float rowWidth = 880f;
            const float rowHeight = 92f;

            RectTransform row = CreateRect(column, $"{name}Row", new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(rowWidth, rowHeight));
            SetLayoutSize(row, rowWidth, rowHeight);

            CreateText(row, "Caption", _bodyLight, BodySize, TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(210f, -22f), new Vector2(420f, 30f),
                label, InkDim).characterSpacing = 4f;

            valueLabel = CreateText(row, "Value", _heading, 26f, TextAlignmentOptions.Right,
                new Vector2(1f, 1f), new Vector2(-90f, -22f), new Vector2(180f, 32f), "0%", Cyan);

            RectTransform sliderRect = CreateRect(row, "Slider", new Vector2(0.5f, 0f),
                new Vector2(0f, 30f), new Vector2(rowWidth, 30f));

            var slider = sliderRect.gameObject.AddComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;
            slider.wholeNumbers = false;

            // Track
            Image background = AddImage(sliderRect, "Track", _pillFill, new Color(1f, 1f, 1f, 0.10f), 0f);
            RectTransform backgroundRect = background.rectTransform;
            backgroundRect.anchorMin = new Vector2(0f, 0.5f);
            backgroundRect.anchorMax = new Vector2(1f, 0.5f);
            backgroundRect.sizeDelta = new Vector2(0f, 14f);
            backgroundRect.anchoredPosition = Vector2.zero;

            // Filled portion
            RectTransform fillArea = CreateRect(sliderRect, "Fill Area", new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero);
            fillArea.anchorMin = new Vector2(0f, 0.5f);
            fillArea.anchorMax = new Vector2(1f, 0.5f);
            fillArea.sizeDelta = new Vector2(-26f, 14f);
            fillArea.anchoredPosition = Vector2.zero;

            Image fill = AddImage(fillArea, "Fill", _pillFill, Cyan, 0f);
            RectTransform fillRect = fill.rectTransform;
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(1f, 1f);
            fillRect.sizeDelta = new Vector2(26f, 0f);

            // Handle
            RectTransform handleArea = CreateRect(sliderRect, "Handle Slide Area", new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero);
            handleArea.anchorMin = Vector2.zero;
            handleArea.anchorMax = Vector2.one;
            handleArea.sizeDelta = new Vector2(-30f, 0f);
            handleArea.anchoredPosition = Vector2.zero;

            RectTransform handleRect = CreateRect(handleArea, "Handle", new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(34f, 34f));
            var handle = handleRect.gameObject.AddComponent<Image>();
            handle.sprite = _circleFill;
            handle.color = Ink;
            handle.raycastTarget = true;

            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;

            return slider;
        }

        // ==================================================================== HUD

        private sealed class Hud
        {
            public CanvasGroup Group;
            public TMP_Text TurnLabel, RoundLabel, MovesLabel, StatusLabel;
            public Image TurnSwatch, TurnBanner;
            public Transform TurnPunch;
            public GameObject RoundCounterRoot;
            public CanvasGroup StatusGroup;
            public Neon MenuButton;
            public List<HudCard> Cards = new List<HudCard>(4);
            public ChatPanel Chat;
        }

        private sealed class ChatPanel
        {
            public GameObject Root;
            public Neon Open, Close;
            public GameObject UnreadBadge;
            public TMP_Text[] LogLines = new TMP_Text[ChatLogLines];
            public TMP_Text Hint;
            public Neon[] Phrases = new Neon[TriggleUISprites.EmoteCount];
            public Image[] PhraseEmotes = new Image[TriggleUISprites.EmoteCount];
        }

        private const int ChatLogLines = 6;

        /// <summary>
        /// The chat panel: a slim tab on the left edge that opens a log and a grid of quick-chat phrases.
        /// </summary>
        /// <remarks>
        /// Sized and placed to fit the gap between the top-left and bottom-left player cards, which
        /// occupy the corners down to y=208 and up from y=818 in canvas units. The panel spans roughly
        /// 290 to 790, so it touches neither at any supported aspect - the canvas is always 1080 tall.
        /// <para>
        /// It starts collapsed and <see cref="ChatPanelController"/> keeps it that way until tapped. An
        /// always-open panel would cover part of the board and, being a raycast target, would swallow
        /// peg clicks in that area.
        /// </para>
        /// </remarks>
        private static ChatPanel BuildChat(RectTransform parent)
        {
            var refs = new ChatPanel();

            // --- collapsed tab -------------------------------------------------
            refs.Open = CreateNeonButton(parent, "ChatTab", "▸", _heading, 30f,
                new Vector2(0f, 0.5f), new Vector2(52f, 0f), new Vector2(56f, 120f),
                Cyan, Coral, Cyan);

            Image badge = AddImage(refs.Open.Root, "UnreadBadge", _circleFill, Coral, 0f,
                new Vector2(18f, 42f));
            badge.rectTransform.anchorMin = badge.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            badge.rectTransform.sizeDelta = new Vector2(18f, 18f);
            refs.UnreadBadge = badge.gameObject;

            // --- expanded panel ------------------------------------------------
            Neon card = CreateNeon(parent, "ChatPanel", new Vector2(0f, 0.5f), new Vector2(224f, 0f),
                new Vector2(400f, 500f), CardFill, Cyan, Coral, false, 0.3f);
            refs.Root = card.Root.gameObject;
            RectTransform c = card.Root;

            CreateText(c, "Header", _heading, 24f, TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(120f, -34f), new Vector2(200f, 30f), "CHAT", Cyan)
                .characterSpacing = 6f;

            refs.Close = CreateNeonButton(c, "CloseButton", "×", _heading, 28f,
                new Vector2(1f, 1f), new Vector2(-40f, -34f), new Vector2(48f, 48f),
                InkFaint, InkFaint, InkDim);

            // --- message log ---------------------------------------------------
            RectTransform log = CreateColumn(c, "Log", new Vector2(0.5f, 1f),
                new Vector2(0f, -160f), new Vector2(348f, 192f), 4f, TextAnchor.LowerLeft);

            for (int i = 0; i < ChatLogLines; i++)
            {
                refs.LogLines[i] = CreateText(log, $"Line{i}", _bodyLight, 17f,
                    TextAlignmentOptions.Left, Vector2.zero, Vector2.zero, new Vector2(348f, 24f),
                    string.Empty, Ink);

                refs.LogLines[i].enableWordWrapping = false;
                refs.LogLines[i].overflowMode = TextOverflowModes.Ellipsis;
                SetLayoutSize(refs.LogLines[i].rectTransform, 348f, 24f);
            }

            refs.Hint = CreateText(c, "Hint", _bodyLight, 14f, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0.5f), new Vector2(0f, -18f), new Vector2(348f, 34f),
                string.Empty, InkFaint);
            refs.Hint.enableWordWrapping = true;

            // --- quick-chat grid -----------------------------------------------
            RectTransform grid = CreateRect(c, "Phrases", new Vector2(0.5f, 0f),
                new Vector2(0f, 130f), new Vector2(360f, 210f));

            var layout = grid.gameObject.AddComponent<GridLayoutGroup>();
            layout.cellSize = new Vector2(172f, 60f);
            layout.spacing = new Vector2(12f, 12f);
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = 2;
            layout.childAlignment = TextAnchor.UpperCenter;

            for (int i = 0; i < TriggleUISprites.EmoteCount; i++)
            {
                Neon phrase = CreateNeonButton(grid, $"Phrase{i}", string.Empty, _body, 16f,
                    Vector2.zero, Vector2.zero, new Vector2(172f, 60f), Cyan, Coral, Ink);

                // The label is offset to leave room for the emote at the left of the button.
                phrase.Label.rectTransform.anchorMin = new Vector2(0f, 0f);
                phrase.Label.rectTransform.anchorMax = new Vector2(1f, 1f);
                phrase.Label.rectTransform.offsetMin = new Vector2(48f, 0f);
                phrase.Label.rectTransform.offsetMax = new Vector2(-8f, 0f);
                phrase.Label.alignment = TextAlignmentOptions.Left;
                phrase.Label.enableWordWrapping = false;
                phrase.Label.overflowMode = TextOverflowModes.Ellipsis;

                Image emote = AddImage(phrase.Root, "Emote", Avatars[0], Ink, 0f, new Vector2(-56f, 0f));
                emote.rectTransform.anchorMin = emote.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                emote.rectTransform.sizeDelta = new Vector2(30f, 30f);
                emote.sprite = TriggleUISprites.Get(TriggleUISprites.EmotePath(i));
                emote.type = Image.Type.Simple;
                emote.preserveAspect = true;

                refs.Phrases[i] = phrase;
                refs.PhraseEmotes[i] = emote;
            }

            // The tab and the panel are alternatives, and ChatPanelController only picks one at runtime.
            // Left on in the saved scene they sit on top of each other.
            card.Root.gameObject.SetActive(false);

            return refs;
        }

        private sealed class HudCard
        {
            public GameObject Root;
            public Image Background, Swatch, Avatar;
            public TMP_Text NameLabel, ScoreLabel, RoundsWonLabel;
            public GameObject ActiveMarker;
        }

        private static Hud BuildHud(RectTransform parent, PlayerColorPalette palette)
        {
            var hud = new Hud();
            RectTransform panel = CreateFullScreen(parent, "HUD", out hud.Group);

            // --- title chip ---------------------------------------------------
            Neon titleChip = CreateNeon(panel, "TitleChip", new Vector2(0.5f, 1f), new Vector2(0f, -62f),
                new Vector2(420f, 84f), new Color(0.10f, 0.12f, 0.17f, 0.80f), Cyan, Coral);
            CreateText(titleChip.Root, "TitleLabel", _display, 44f, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(420f, 56f), "TRIGGLE", Coral)
                .characterSpacing = 4f;

            // --- round chip (hidden for a single-round match) -----------------
            Neon roundChip = CreateNeon(panel, "RoundCounter", new Vector2(1f, 1f),
                new Vector2(-320f, -62f), new Vector2(250f, 68f), SurfaceFill, Cyan, Cyan);
            hud.RoundCounterRoot = roundChip.Root.gameObject;
            hud.RoundLabel = CreateText(roundChip.Root, "RoundLabel", _heading, 26f,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(250f, 36f), "ROUND 1/10", Ink);

            // --- menu button --------------------------------------------------
            hud.MenuButton = CreateNeonButton(panel, "HudMenuButton", "II", _heading, 26f,
                new Vector2(1f, 1f), new Vector2(-100f, -62f), new Vector2(72f, 72f),
                Cyan, Coral, Ink);

            // Centred under the title chip, not tucked under the round counter: the top-right player
            // card reaches up to y=-94, so anything on the right below that sits on top of it. The
            // centre column is clear, because the cards are inset from both edges.
            hud.MovesLabel = CreateText(panel, "MovesRemaining", _bodyLight, 17f,
                TextAlignmentOptions.Center, new Vector2(0.5f, 1f), new Vector2(0f, -134f),
                new Vector2(400f, 26f), "48 bands left", InkFaint);

            // --- four corner player cards -------------------------------------
            // Symmetric insets: the same distance from each corner, with the top row pushed below the
            // header chips so nothing collides with the title, round counter or pause button.
            const float insetX = 224f;
            const float topInsetY = 178f;
            const float bottomInsetY = 124f;

            var corners = new[]
            {
                new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(1f, 0f)
            };
            var offsets = new[]
            {
                new Vector2(insetX, -topInsetY), new Vector2(-insetX, -topInsetY),
                new Vector2(insetX, bottomInsetY), new Vector2(-insetX, bottomInsetY)
            };

            for (int i = 0; i < 4; i++)
                hud.Cards.Add(BuildHudCard(panel, i, corners[i], offsets[i], palette));

            // --- turn banner --------------------------------------------------
            Neon banner = CreateNeon(panel, "TurnBanner", new Vector2(0.5f, 0f), new Vector2(0f, 82f),
                new Vector2(820f, 92f), GlassFill, Cyan, Cyan);
            hud.TurnBanner = banner.Fill;
            hud.TurnPunch = banner.Root;

            hud.TurnSwatch = CreatePanel(banner.Root, "TurnSwatch", _panelFill, new Vector2(0f, 0.5f),
                new Vector2(52f, 0f), new Vector2(40f, 40f), Color.white);

            hud.TurnLabel = CreateText(banner.Root, "TurnLabel", _heading, 32f,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), new Vector2(24f, 0f),
                new Vector2(680f, 48f), "Player 1's Turn - Stretch 4 Pegs", Ink);

            // --- status toast -------------------------------------------------
            // The only element authored wider than the narrowest landscape canvas (1350 units at 5:4),
            // so it stretches with a margin instead of carrying a fixed 1400 width.
            RectTransform toast = CreateRect(panel, "StatusToast", new Vector2(0.5f, 0f),
                new Vector2(0f, 196f), new Vector2(1400f, 46f));
            toast.anchorMin = new Vector2(0f, 0f);
            toast.anchorMax = new Vector2(1f, 0f);
            toast.offsetMin = new Vector2(80f, 173f);
            toast.offsetMax = new Vector2(-80f, 219f);

            hud.StatusGroup = toast.gameObject.AddComponent<CanvasGroup>();
            hud.StatusGroup.blocksRaycasts = false;

            hud.StatusLabel = CreateText(toast, "StatusLabel", _heading, 28f,
                TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(1400f, 46f), string.Empty, Ink);
            Stretch(hud.StatusLabel.rectTransform, 0f);

            // --- chat ---------------------------------------------------------
            hud.Chat = BuildChat(panel);

            return hud;
        }

        private static HudCard BuildHudCard(RectTransform parent, int index, Vector2 anchor,
                                             Vector2 offset, PlayerColorPalette palette)
        {
            var card = new HudCard();
            Color color = palette.GetColorBySlot(index);

            Neon root = CreateNeon(parent, $"PlayerCard_{index + 1}", anchor, offset,
                new Vector2(392f, 168f), new Color(0.10f, 0.12f, 0.17f, 0.82f), color, Coral, false, 0.3f);

            card.Root = root.Root.gameObject;
            card.Background = root.Fill;

            Neon avatarChip = CreateNeon(root.Root, "AvatarChip", new Vector2(0f, 1f),
                new Vector2(58f, -50f), new Vector2(72f, 72f), SurfaceFill, color, color, false);

            RectTransform avatarRect = CreateRect(avatarChip.Root, "Avatar", new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(48f, 48f));
            var avatarImage = avatarRect.gameObject.AddComponent<Image>();
            avatarImage.sprite = Avatars[index % Avatars.Length];
            avatarImage.color = color;
            avatarImage.preserveAspect = true;
            avatarImage.raycastTarget = false;
            card.Avatar = avatarImage;

            card.NameLabel = CreateText(root.Root, "NameLabel", _body, 25f, TextAlignmentOptions.Left,
                new Vector2(0f, 1f), new Vector2(258f, -44f), new Vector2(240f, 32f),
                $"Player {index + 1}", color);
            card.NameLabel.overflowMode = TextOverflowModes.Ellipsis;

            card.Swatch = CreatePanel(root.Root, "Swatch", _panelFill, new Vector2(0f, 1f),
                new Vector2(158f, -86f), new Vector2(30f, 30f), color);

            card.ScoreLabel = CreateText(root.Root, "ScoreLabel", _display, 46f,
                TextAlignmentOptions.Left, new Vector2(0f, 0f), new Vector2(212f, 44f),
                new Vector2(300f, 58f), "0", Ink);

            card.RoundsWonLabel = CreateText(root.Root, "RoundsWonLabel", _bodyLight, 16f,
                TextAlignmentOptions.Right, new Vector2(1f, 0f), new Vector2(-84f, 40f),
                new Vector2(150f, 24f), "0 rounds", InkFaint);

            card.ActiveMarker = AddImage(root.Root, "ActiveRing", _panelOutline, Cyan, -6f).gameObject;

            return card;
        }

        // ==================================================================== round + match panels

        private sealed class RoundPanel
        {
            public CanvasGroup Group;
            public TMP_Text Title, Subtitle, Standings, ContinueLabel;
            public Neon ContinueButton;
        }

        private static RoundPanel BuildRoundPanel(RectTransform parent)
        {
            var refs = new RoundPanel();
            RectTransform panel = CreateFullScreen(parent, "RoundSummaryPanel", out refs.Group);
            AddScrim(panel, Scrim);

            Neon card = CreateNeon(panel, "Card", new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(840f, 620f), CardFill, Cyan, Coral, false, 0.35f);
            RectTransform c = card.Root;

            refs.Title = CreateText(c, "Title", _display, H1, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0.5f), new Vector2(0f, 210f), new Vector2(780f, 90f),
                "PLAYER 1 WINS ROUND 1", Ink);
            refs.Title.enableWordWrapping = true;

            refs.Subtitle = CreateText(c, "Subtitle", _bodyLight, 21f, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0.5f), new Vector2(0f, 146f), new Vector2(700f, 28f),
                "Round 1 of 3 complete", InkDim);

            refs.Standings = CreateText(c, "Standings", _heading, 27f, TextAlignmentOptions.Top,
                new Vector2(0.5f, 0.5f), new Vector2(0f, -14f), new Vector2(660f, 230f),
                string.Empty, Ink);
            refs.Standings.lineSpacing = 14f;

            refs.ContinueButton = CreateNeonButton(c, "ContinueButton", "NEXT ROUND", _display, 34f,
                new Vector2(0.5f, 0f), new Vector2(0f, 76f), new Vector2(440f, 88f),
                Green, Green, Color.white, new Color(Green.r, Green.g, Green.b, 0.22f));
            refs.ContinueLabel = refs.ContinueButton.Label;

            return refs;
        }

        private sealed class MatchPanel
        {
            public CanvasGroup Group;
            public Image Accent;
            public TMP_Text Title, Subtitle, Standings;
            public Neon Rematch, Menu;
        }

        private static MatchPanel BuildMatchPanel(RectTransform parent)
        {
            var refs = new MatchPanel();
            RectTransform panel = CreateFullScreen(parent, "MatchPanel", out refs.Group);
            AddScrim(panel, Scrim);

            Neon card = CreateNeon(panel, "Card", new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(900f, 760f), CardFill, Gold, Gold, false, 0.4f);
            RectTransform c = card.Root;

            // Accent bar; GameUIController tints it gold / red / neutral per outcome.
            refs.Accent = CreatePanel(c, "OutcomeAccent", _pillFill, new Vector2(0.5f, 1f),
                new Vector2(0f, -72f), new Vector2(220f, 7f), Gold);

            refs.Title = CreateText(c, "Title", _display, 74f, TextAlignmentOptions.Center,
                new Vector2(0.5f, 1f), new Vector2(0f, -150f), new Vector2(840f, 100f),
                "PLAYER 1 WINS!", Gold);
            refs.Title.enableWordWrapping = true;

            refs.Subtitle = CreateText(c, "Subtitle", _bodyLight, 22f, TextAlignmentOptions.Center,
                new Vector2(0.5f, 1f), new Vector2(0f, -218f), new Vector2(760f, 30f),
                "3 of 5 rounds won", InkDim);

            refs.Standings = CreateText(c, "Standings", _heading, 30f, TextAlignmentOptions.Top,
                new Vector2(0.5f, 0.5f), new Vector2(0f, -30f), new Vector2(700f, 280f),
                string.Empty, Ink);
            refs.Standings.lineSpacing = 16f;

            refs.Rematch = CreateNeonButton(c, "RematchButton", "REMATCH", _display, 34f,
                new Vector2(0.5f, 0f), new Vector2(0f, 148f), new Vector2(420f, 88f),
                Green, Green, Color.white, new Color(Green.r, Green.g, Green.b, 0.22f));

            refs.Menu = CreateNeonButton(c, "MainMenuButton", "Main Menu", _body, BodySize,
                new Vector2(0.5f, 0f), new Vector2(0f, 66f), new Vector2(360f, 62f),
                Gold, Gold, Gold);

            return refs;
        }

        // ==================================================================== pause

        private sealed class PausePanel
        {
            public CanvasGroup Group;
            public GameObject MainButtons;
            public GameObject ConfirmGroup;
            public Neon Resume, Settings, Restart, MainMenu, ConfirmYes, ConfirmNo;
            public TMP_Text ConfirmLabel, ContextLabel;
        }

        /// <summary>
        /// Pause overlay. Main Menu sits behind a confirmation step, because reaching it by accident
        /// throws the whole match away.
        /// </summary>
        private static PausePanel BuildPausePanel(RectTransform parent)
        {
            var refs = new PausePanel();
            RectTransform panel = CreateFullScreen(parent, "PausePanel", out refs.Group);
            AddScrim(panel, Scrim);

            Neon card = CreateNeon(panel, "Card", new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(680f, 640f), CardFill, Cyan, Coral, false, 0.35f);
            RectTransform c = card.Root;

            Neon header = CreateNeon(c, "HeaderChip", new Vector2(0.5f, 1f), new Vector2(0f, 8f),
                new Vector2(340f, 84f), SurfaceFill, Cyan, Coral);
            CreateText(header.Root, "HeaderLabel", _heading, H2, TextAlignmentOptions.Center,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(340f, 44f), "PAUSED", Cyan);

            refs.ContextLabel = CreateText(c, "ContextLabel", _bodyLight, 20f,
                TextAlignmentOptions.Center, new Vector2(0.5f, 1f), new Vector2(0f, -104f),
                new Vector2(600f, 28f), "Match in progress", InkDim);

            // --- main button list ---------------------------------------------
            RectTransform buttons = CreateColumn(c, "MainButtons", new Vector2(0.5f, 0.5f),
                new Vector2(0f, -46f), new Vector2(520f, 420f), 24f);
            refs.MainButtons = buttons.gameObject;

            refs.Resume = CreateNeonButton(buttons, "ResumeButton", "RESUME", _display, 34f,
                Vector2.zero, Vector2.zero, new Vector2(440f, 92f),
                Green, Green, Color.white, new Color(Green.r, Green.g, Green.b, 0.22f));
            SetLayoutSize(refs.Resume.Root, 440f, 92f);

            refs.Restart = CreateNeonButton(buttons, "RestartButton", "Restart Match", _body, BodySize,
                Vector2.zero, Vector2.zero, new Vector2(400f, 72f), Cyan, Coral, Ink);
            SetLayoutSize(refs.Restart.Root, 400f, 72f);

            refs.Settings = CreateNeonButton(buttons, "SettingsButton", "Settings", _body, BodySize,
                Vector2.zero, Vector2.zero, new Vector2(400f, 72f), Cyan, Coral, Ink);
            SetLayoutSize(refs.Settings.Root, 400f, 72f);

            refs.MainMenu = CreateNeonButton(buttons, "MainMenuButton", "Main Menu", _body, BodySize,
                Vector2.zero, Vector2.zero, new Vector2(400f, 72f), Coral, Coral, Coral);
            SetLayoutSize(refs.MainMenu.Root, 400f, 72f);

            // --- confirmation -------------------------------------------------
            RectTransform confirm = CreateColumn(c, "ConfirmGroup", new Vector2(0.5f, 0.5f),
                new Vector2(0f, -46f), new Vector2(560f, 420f), 24f);
            refs.ConfirmGroup = confirm.gameObject;

            refs.ConfirmLabel = CreateText(confirm, "ConfirmLabel", _bodyLight, 22f,
                TextAlignmentOptions.Center, Vector2.zero, Vector2.zero,
                new Vector2(520f, 96f), string.Empty, Ink);
            refs.ConfirmLabel.enableWordWrapping = true;
            SetLayoutSize(refs.ConfirmLabel.rectTransform, 520f, 96f);

            refs.ConfirmYes = CreateNeonButton(confirm, "ConfirmYes", "Leave Match", _body, BodySize,
                Vector2.zero, Vector2.zero, new Vector2(400f, 78f), Coral, Coral, Coral);
            SetLayoutSize(refs.ConfirmYes.Root, 400f, 78f);

            refs.ConfirmNo = CreateNeonButton(confirm, "ConfirmNo", "Keep Playing", _display, 30f,
                Vector2.zero, Vector2.zero, new Vector2(400f, 86f),
                Green, Green, Color.white, new Color(Green.r, Green.g, Green.b, 0.22f));
            SetLayoutSize(refs.ConfirmNo.Root, 400f, 86f);

            confirm.gameObject.SetActive(false);
            return refs;
        }

        private static void WirePause(PausePanelController controller, Context context,
                                       MainMenuController menu, SettingsPanelController settings,
                                       PausePanel refs, Button pauseButton)
        {
            using var so = new SerializedWiring(controller);

            so.Ref("flowController", context.Flow);
            so.Ref("matchController", context.Match);
            so.Ref("mainMenu", menu);
            so.Ref("settings", settings);

            so.Ref("panel", refs.Group);
            so.Ref("pauseButton", pauseButton);
            so.Ref("resumeButton", refs.Resume.Button);
            so.Ref("settingsButton", refs.Settings.Button);
            so.Ref("restartRoundButton", refs.Restart.Button);
            so.Ref("mainMenuButton", refs.MainMenu.Button);

            so.Ref("mainButtonGroup", refs.MainButtons);
            so.Ref("confirmGroup", refs.ConfirmGroup);
            so.Ref("confirmYesButton", refs.ConfirmYes.Button);
            so.Ref("confirmNoButton", refs.ConfirmNo.Button);
            so.Ref("confirmLabel", refs.ConfirmLabel);
            so.Ref("contextLabel", refs.ContextLabel);
        }

        private static void WireChat(ChatPanelController controller, Context context, ChatPanel refs)
        {
            using var so = new SerializedWiring(controller);

            so.Ref("networkMatch", context.Net);
            so.Ref("palette", context.Palette);

            so.Ref("panel", refs.Root);
            so.Ref("openButton", refs.Open.Button);
            so.Ref("closeButton", refs.Close.Button);
            so.Ref("unreadBadge", refs.UnreadBadge);
            so.Ref("hintLabel", refs.Hint);

            so.ArraySize("logLines", ChatLogLines);
            for (int i = 0; i < ChatLogLines; i++)
                so.Ref($"logLines.Array.data[{i}]", refs.LogLines[i]);

            so.ArraySize("phraseButtons", TriggleUISprites.EmoteCount);
            for (int i = 0; i < TriggleUISprites.EmoteCount; i++)
            {
                string path = $"phraseButtons.Array.data[{i}]";

                so.Ref($"{path}.button", refs.Phrases[i].Button);
                so.Ref($"{path}.emote", refs.PhraseEmotes[i]);
                so.Ref($"{path}.label", refs.Phrases[i].Label);
            }
        }

        // ==================================================================== wiring

        private static void WireMenu(MainMenuController controller, Context context,
                                      LobbyController lobby, SettingsPanelController settings,
                                      MultiplayerPanelController multiplayerController,
                                      RootMenu root, Lobby lobbyRefs, HowToPlay howTo,
                                      MultiplayerScreen multiplayerRefs, Hud hud)
        {
            using var so = new SerializedWiring(controller);

            so.Ref("flowController", context.Flow);
            so.Ref("matchController", context.Match);
            so.Ref("lobby", lobby);
            so.Ref("settings", settings);

            so.Ref("rootMenuPanel", root.Group);
            so.Ref("lobbyPanel", lobbyRefs.Group);
            so.Ref("howToPlayPanel", howTo.Group);
            so.Ref("multiplayerPanel", multiplayerRefs.Group);
            so.Ref("multiplayer", multiplayerController);
            so.Ref("hudPanel", hud.Group);

            so.Ref("playLocalButton", root.PlayLocal.Button);
            so.Ref("playAiButton", root.PlayAi.Button);
            so.Ref("playOnlineButton", root.PlayOnline.Button);
            so.Ref("howToPlayButton", root.HowToPlay.Button);
            so.Ref("settingsButton", root.Settings.Button);
            so.Ref("quitButton", root.Quit.Button);
            so.Ref("playAiLabel", root.PlayAi.Label);
            so.Ref("playAiSubLabel", root.AiSubLabel);

            so.Ref("howToPlayCloseButton", howTo.Close.Button);
            so.Ref("howToPlayBody", howTo.Body);
        }

        private static void WireLobby(LobbyController controller, Context context,
                                       MainMenuController menu, Lobby refs)
        {
            using var so = new SerializedWiring(controller);

            so.Ref("flowController", context.Flow);
            so.Ref("matchController", context.Match);
            so.Ref("palette", context.Palette);
            so.Ref("mainMenu", menu);

            so.ArraySize("playerCountButtons", 3);
            so.ArraySize("playerCountOutlines", 3);
            so.ArraySize("playerCountLabels", 3);
            for (int i = 0; i < 3; i++)
            {
                so.Ref($"playerCountButtons.Array.data[{i}]", refs.CountButtons[i].Button);
                so.Ref($"playerCountOutlines.Array.data[{i}]", refs.CountButtons[i].OutlineA);
                so.Ref($"playerCountLabels.Array.data[{i}]", refs.CountButtons[i].Label);
            }

            so.ArraySize("seatRows", 4);
            for (int i = 0; i < 4; i++)
            {
                LobbySeat seat = refs.Seats[i];
                string path = $"seatRows.Array.data[{i}]";

                so.Ref($"{path}.root", seat.Root);
                so.Ref($"{path}.outline", seat.Outline);
                so.Ref($"{path}.avatar", seat.Avatar);
                so.Ref($"{path}.nameInput", seat.NameInput);
                so.Ref($"{path}.kindButton", seat.KindButton);
                so.Ref($"{path}.kindLabel", seat.KindLabel);

                so.ArraySize($"{path}.colorButtons", PlayerProfiles.ColorSlotCount);
                so.ArraySize($"{path}.colorSelectionMarkers", PlayerProfiles.ColorSlotCount);

                for (int s = 0; s < PlayerProfiles.ColorSlotCount; s++)
                {
                    so.Ref($"{path}.colorButtons.Array.data[{s}]", seat.ColorButtons[s]);
                    so.Ref($"{path}.colorSelectionMarkers.Array.data[{s}]", seat.ColorMarkers[s]);
                }
            }

            so.Ref("roundsDownButton", refs.RoundsDown.Button);
            so.Ref("roundsUpButton", refs.RoundsUp.Button);
            so.Ref("roundsValueLabel", refs.RoundsValue);
            so.Ref("roundsCaptionLabel", refs.RoundsCaption);

            so.Ref("difficultyRoot", refs.DifficultyRoot);
            so.Ref("difficultyDownButton", refs.DifficultyDown.Button);
            so.Ref("difficultyUpButton", refs.DifficultyUp.Button);
            so.Ref("difficultyValueLabel", refs.DifficultyValue);
            so.Ref("difficultyCaptionLabel", refs.DifficultyCaption);

            so.Ref("startButton", refs.Start.Button);
            so.Ref("backButton", refs.Back.Button);
        }

        private static void WireSettings(SettingsPanelController controller, Context context,
                                          SettingsScreen refs)
        {
            using var so = new SerializedWiring(controller);

            so.Ref("flowController", context.Flow);
            so.Ref("matchController", context.Match);
            so.Ref("themeLibrary", context.Themes);

            so.Ref("panel", refs.Group);
            so.Ref("closeButton", refs.Close.Button);

            so.Ref("audioTabButton", refs.AudioTab.Button);
            so.Ref("boardTabButton", refs.BoardTab.Button);
            so.Ref("audioTabContent", refs.AudioContent);
            so.Ref("boardTabContent", refs.BoardContent);
            so.Ref("audioTabUnderline", refs.AudioUnderline);
            so.Ref("boardTabUnderline", refs.BoardUnderline);
            so.Ref("audioTabLabel", refs.AudioLabel);
            so.Ref("boardTabLabel", refs.BoardLabel);

            so.Ref("masterSlider", refs.Master);
            so.Ref("musicSlider", refs.Music);
            so.Ref("sfxSlider", refs.Sfx);
            so.Ref("masterValueLabel", refs.MasterValue);
            so.Ref("musicValueLabel", refs.MusicValue);
            so.Ref("sfxValueLabel", refs.SfxValue);

            so.ArraySize("themeChips", 6);
            for (int i = 0; i < 6; i++)
            {
                ThemeChipRefs chip = refs.Themes[i];
                string path = $"themeChips.Array.data[{i}]";

                so.Ref($"{path}.root", chip.Root);
                so.Ref($"{path}.button", chip.Button);
                so.Ref($"{path}.swatch", chip.Swatch);
                so.Ref($"{path}.accent", chip.Accent);
                so.Ref($"{path}.selectionMarker", chip.Marker);
                so.Ref($"{path}.label", chip.Label);
            }

            so.Ref("sizeDownButton", refs.SizeDown.Button);
            so.Ref("sizeUpButton", refs.SizeUp.Button);
            so.Ref("sizeValueLabel", refs.SizeValue);
            so.Ref("sizeCaptionLabel", refs.SizeCaption);
            so.Ref("lockedNotice", refs.LockedNotice);
            so.Ref("lockedNoticeLabel", refs.LockedLabel);
        }

        private static void WireHud(GameUIController controller, Context context,
                                     MainMenuController menu, Hud hud, RoundPanel round,
                                     MatchPanel match)
        {
            using var so = new SerializedWiring(controller);

            so.Ref("flowController", context.Flow);
            so.Ref("matchController", context.Match);
            so.Ref("palette", context.Palette);
            so.Ref("mainMenu", menu);

            so.Ref("turnLabel", hud.TurnLabel);
            so.Ref("turnColorSwatch", hud.TurnSwatch);
            so.Ref("turnBanner", hud.TurnBanner);
            so.Ref("turnBannerPunchTarget", hud.TurnPunch);
            so.Ref("roundLabel", hud.RoundLabel);
            so.Ref("roundCounterRoot", hud.RoundCounterRoot);
            so.Ref("movesRemainingLabel", hud.MovesLabel);
            so.Ref("statusLabel", hud.StatusLabel);
            so.Ref("statusGroup", hud.StatusGroup);
            so.Float("statusDuration", 2.2f);

            so.ArraySize("scoreSlots", 4);
            for (int i = 0; i < hud.Cards.Count; i++)
            {
                HudCard card = hud.Cards[i];
                string path = $"scoreSlots.Array.data[{i}]";

                so.Ref($"{path}.root", card.Root);
                so.Ref($"{path}.background", card.Background);
                so.Ref($"{path}.swatch", card.Swatch);
                so.Ref($"{path}.nameLabel", card.NameLabel);
                so.Ref($"{path}.scoreLabel", card.ScoreLabel);
                so.Ref($"{path}.roundsWonLabel", card.RoundsWonLabel);
                so.Ref($"{path}.activeMarker", card.ActiveMarker);
            }

            so.Ref("roundPanel", round.Group);
            so.Ref("roundPanelTitle", round.Title);
            so.Ref("roundPanelSubtitle", round.Subtitle);
            so.Ref("roundPanelStandings", round.Standings);
            so.Ref("roundPanelContinueButton", round.ContinueButton.Button);
            so.Ref("roundPanelContinueLabel", round.ContinueLabel);

            so.Ref("matchPanel", match.Group);
            so.Ref("matchTitle", match.Title);
            so.Ref("matchSubtitle", match.Subtitle);
            so.Ref("matchStandings", match.Standings);
            so.Ref("matchAccent", match.Accent);
            so.Ref("rematchButton", match.Rematch.Button);
            so.Ref("matchMenuButton", match.Menu.Button);
        }

        // ==================================================================== primitives

        /// <summary>
        /// A full-screen panel. Returns the <b>safe-area content rect</b>, which is where every caller
        /// builds; the outer rect it hangs off is reserved for the scrim.
        /// </summary>
        /// <remarks>
        /// The scrim must stay outside the inset or the board shows through the strip beside a notch,
        /// and it must stay <i>behind</i> the content or it swallows every click aimed at a button.
        /// <see cref="AddScrim"/> guarantees the second part by pinning itself to sibling index 0, which
        /// is why this can create the content rect first without caring what order callers use.
        /// </remarks>
        private static RectTransform CreateFullScreen(RectTransform parent, string name,
                                                       out CanvasGroup group)
        {
            RectTransform rect = CreateRect(parent, name, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            Stretch(rect, 0f);

            group = rect.gameObject.AddComponent<CanvasGroup>();

            RectTransform safe = CreateRect(rect, "SafeArea", new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero);
            Stretch(safe, 0f);
            safe.gameObject.AddComponent<CanvasSafeArea>();

            return safe;
        }

        /// <summary>
        /// Full-screen backdrop that also swallows clicks aimed at the board behind it.
        /// </summary>
        /// <param name="content">
        /// The panel's safe-area content rect. The scrim is attached to that rect's <b>parent</b>, so it
        /// covers the display cutout too rather than stopping at the safe area.
        /// </param>
        /// <remarks>
        /// Pinned to sibling index 0. uGUI draws in hierarchy order and hit-tests in reverse, so a
        /// full-screen raycast target sitting after the content covers the whole panel and eats every
        /// click - the buttons render but nothing responds. Forcing it to the back here means no future
        /// change to the order things are built in can reintroduce that.
        /// </remarks>
        private static Image AddScrim(RectTransform content, Color tint)
        {
            var parent = content.parent as RectTransform;
            if (parent == null) parent = content;

            Image scrim = CreatePanel(parent, "Scrim", null, new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero, tint);
            Stretch(scrim.rectTransform, 0f);
            scrim.rectTransform.SetSiblingIndex(0);

            if (_glassBackdrop != null)
            {
                // Heavy blur turns the board into an unreadable wash instead of a distraction, which is
                // the point: the menu should not look like it is sitting on a live game.
                scrim.material = _glassBackdrop;
                scrim.color = Color.white;
                scrim.sprite = null;
            }
            else if (_gradient != null)
            {
                scrim.sprite = _gradient;
                scrim.type = Image.Type.Simple;
            }

            scrim.raycastTarget = true;
            return scrim;
        }

        private static void Stretch(RectTransform rect, float padding)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(padding, padding);
            rect.offsetMax = new Vector2(-padding, -padding);
        }

        /// <summary>
        /// Vertical stack driven by Unity's layout system. Children are positioned and sized from their
        /// LayoutElement preferred sizes, which makes overlap impossible by construction - hand-computed
        /// anchoredPositions do not, and got it wrong.
        /// </summary>
        private static RectTransform CreateColumn(RectTransform parent, string name, Vector2 anchor,
                                                   Vector2 position, Vector2 size, float spacing,
                                                   TextAnchor align = TextAnchor.MiddleCenter)
        {
            RectTransform rect = CreateRect(parent, name, anchor, position, size);

            var group = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            group.spacing = spacing;
            group.childAlignment = align;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;

            return rect;
        }

        /// <summary>Horizontal equivalent of <see cref="CreateColumn"/>.</summary>
        private static RectTransform CreateRow(RectTransform parent, string name, Vector2 anchor,
                                                Vector2 position, Vector2 size, float spacing,
                                                TextAnchor align = TextAnchor.MiddleCenter)
        {
            RectTransform rect = CreateRect(parent, name, anchor, position, size);

            var group = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            group.spacing = spacing;
            group.childAlignment = align;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;

            return rect;
        }

        /// <summary>
        /// Declares a layout child's size. Required: a RectTransform with no ILayoutElement reports a
        /// preferred size of zero, so a layout group would stack every child at the same spot.
        /// </summary>
        private static void SetLayoutSize(RectTransform rect, float width, float height)
        {
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.preferredWidth = width;
            element.preferredHeight = height;
            element.flexibleWidth = 0f;
            element.flexibleHeight = 0f;
        }

        private static RectTransform CreateRect(RectTransform parent, string name, Vector2 anchor,
                                                 Vector2 anchoredPosition, Vector2 size)
        {
            var go = new GameObject(name);
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);

            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            return rect;
        }

        private static TMP_Text CreateText(RectTransform parent, string name, TMP_FontAsset font,
                                            float fontSize, TextAlignmentOptions alignment, Vector2 anchor,
                                            Vector2 anchoredPosition, Vector2 size, string content,
                                            Color color)
        {
            RectTransform rect = CreateRect(parent, name, anchor, anchoredPosition, size);

            var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null) text.font = font;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.text = content;
            text.color = color;
            text.raycastTarget = false;
            text.enableWordWrapping = false;

            return text;
        }

        private static Image CreatePanel(RectTransform parent, string name, Sprite sprite, Vector2 anchor,
                                          Vector2 anchoredPosition, Vector2 size, Color color)
        {
            RectTransform rect = CreateRect(parent, name, anchor, anchoredPosition, size);

            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;

            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = Image.Type.Sliced;
            }

            return image;
        }

        /// <summary>
        /// TextMeshPro input field with the hierarchy TMP_InputField expects: a masked viewport holding a
        /// placeholder and the live text component.
        /// </summary>
        private static TMP_InputField CreateInputField(RectTransform parent, string name, Vector2 anchor,
                                                        Vector2 anchoredPosition, Vector2 size,
                                                        float fontSize, string placeholderText,
                                                        Color accentColor)
        {
            RectTransform rect = CreateRect(parent, name, anchor, anchoredPosition, size);

            var background = rect.gameObject.AddComponent<Image>();
            background.sprite = _panelFill;
            background.type = Image.Type.Sliced;
            background.color = new Color(1f, 1f, 1f, 0.07f);
            background.raycastTarget = true;

            RectTransform viewport = CreateRect(rect, "Text Area", new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero);
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = new Vector2(20f, 6f);
            viewport.offsetMax = new Vector2(-20f, -6f);
            viewport.gameObject.AddComponent<RectMask2D>();

            TMP_Text placeholder = CreateText(viewport, "Placeholder", _body, fontSize,
                TextAlignmentOptions.Left, new Vector2(0.5f, 0.5f), Vector2.zero, size,
                placeholderText, new Color(accentColor.r, accentColor.g, accentColor.b, 0.45f));
            Stretch(placeholder.rectTransform, 0f);

            TMP_Text text = CreateText(viewport, "Text", _body, fontSize,
                TextAlignmentOptions.Left, new Vector2(0.5f, 0.5f), Vector2.zero, size,
                string.Empty, Ink);
            Stretch(text.rectTransform, 0f);
            text.richText = false;   // names are user input; never interpret them as markup

            var field = rect.gameObject.AddComponent<TMP_InputField>();
            field.targetGraphic = background;
            field.textViewport = viewport;
            field.textComponent = text;
            field.placeholder = placeholder;
            field.fontAsset = _body;
            field.pointSize = fontSize;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.characterLimit = PlayerProfiles.MaxNameLength;
            field.selectionColor = new Color(accentColor.r, accentColor.g, accentColor.b, 0.35f);
            field.caretColor = Ink;
            field.customCaretColor = true;
            field.text = string.Empty;

            return field;
        }

        private static void SetHidden(CanvasGroup group)
        {
            if (group == null) return;

            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;
            group.gameObject.SetActive(false);
        }

        private static void SetVisible(CanvasGroup group)
        {
            if (group == null) return;

            group.alpha = 1f;
            group.blocksRaycasts = true;
            group.interactable = true;
            group.gameObject.SetActive(true);
        }
    }
}
