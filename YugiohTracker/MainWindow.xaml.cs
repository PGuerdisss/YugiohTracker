using Microsoft.Win32;
using SharpDX.DirectInput;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using WpfKey = System.Windows.Input.Key;
using WpfTextAlignment = System.Windows.TextAlignment;

namespace YugiohTracker
{
    public partial class MainWindow : Window
    {
        // --- HOOKS E DLLs ---
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;

        private static LowLevelKeyboardProc _proc = HookCallback;
        private static IntPtr _hookID = IntPtr.Zero;
        private static MainWindow? _instance;
        private DirectInput? _directInput;
        private Joystick? _joystick;
        private Thread? _inputThread;
        private bool _appRodando = true;
        private bool _controleConectado = false;
        private int _teclaPausa = 0;
        private bool _inputsAtivos = true;
        private bool isEnglish = false;

        private bool _jaViuAjuda = false;
        private SlotConfig? _slotSobMouse = null;

        public class RootSaveData
        {
            public int PauseKey { get; set; }
            public double WindowLeft { get; set; }
            public double WindowTop { get; set; }
            public bool HasSeenHelp { get; set; } = false;
            public List<SlotData> Slots { get; set; } = new List<SlotData>();
        }

        public class SlotData
        {
            public string ImagemPath { get; set; } = "";
            public int AtalhoKey { get; set; } = 0;
            public int AtalhoMod { get; set; } = 0;
            public int JoystickBtn { get; set; } = -1;
            public int Valor { get; set; } = 0;
            public int Linha { get; set; }
            public int Coluna { get; set; }
        }

        public class SlotConfig
        {
            public TextBlock ContadorTexto { get; set; } = null!;
            public Image ImagemCarta { get; set; } = null!;
            public string ImagemAtualPath { get; set; } = "";
            public int AtalhoKey { get; set; } = 0;
            public int AtalhoMod { get; set; } = 0;
            public int JoystickBtn { get; set; } = -1;
            public bool KeyEstaPressionada { get; set; } = false;
            public bool PadEstaPressionado { get; set; } = false;
            public long TempoInicioPress { get; set; } = 0;
            public bool ModoSubtracaoAtivo { get; set; } = false;
            public long UltimaRepeticao { get; set; } = 0;
            public Action? MudarImagemAction { get; set; }
        }

        private List<List<SlotConfig>> matrizSlots = new List<List<SlotConfig>>();
        private int colunasAtuais = 0;
        private string autoSavePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "layout_v3.json");

        public MainWindow()
        {
            InitializeComponent();
            _instance = this;
            _directInput = new DirectInput();
            _inputThread = new Thread(InputLoop);
            _inputThread.IsBackground = true;
            _inputThread.Priority = ThreadPriority.Highest;
            _inputThread.Start();
            this.Closing += (s, e) =>
            {
                _appRodando = false; UnhookWindowsHookEx(_hookID);
                if (_joystick != null) { try { _joystick.Unacquire(); _joystick.Dispose(); } catch { } }
                if (_directInput != null) _directInput.Dispose();
                SalvarArquivo(autoSavePath, false);
            };
        }

        // --- FUNÇÃO PARA SALVAR NA PLANILHA (100% Silenciosa) ---
        public static void SalvarCartaNoExcel(string idBruto)
        {
            string caminhoArquivo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Master_FM.csv");
            if (!File.Exists(caminhoArquivo)) return;

            try
            {
                if (int.TryParse(idBruto, out int numId)) idBruto = numId.ToString();

                var linhas = File.ReadAllLines(caminhoArquivo, Encoding.UTF8).ToList();
                if (linhas.Count == 0) return;

                char separador = linhas[0].Contains(";") ? ';' : ',';
                bool atualizou = false;

                for (int i = 1; i < linhas.Count; i++)
                {
                    var colunas = linhas[i].Split(separador);
                    string idNaPlanilha = colunas[0].Trim().Replace("\"", "");

                    if (idNaPlanilha == idBruto && colunas.Length >= 6)
                    {
                        if (colunas[5].Trim().Replace("\"", "") != "Y")
                        {
                            colunas[5] = "Y";
                            linhas[i] = string.Join(separador.ToString(), colunas);
                            atualizou = true;
                        }
                        break;
                    }
                }

                if (atualizou) File.WriteAllLines(caminhoArquivo, linhas, Encoding.UTF8);
            }
            catch { } // Silêncio absoluto em caso de erro
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
            {
                int vkCode = Marshal.ReadInt32(lParam); if (_instance != null) _instance.ProcessarTeclaInstantanea(vkCode);
            }
            if (nCode >= 0 && (wParam == (IntPtr)WM_KEYUP))
            {
                int vkCode = Marshal.ReadInt32(lParam); if (_instance != null) _instance.ProcessarTeclaSolta(vkCode);
            }
            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        private void ProcessarTeclaInstantanea(int vkCode)
        {
            if (_teclaPausa != 0 && vkCode == _teclaPausa) { _inputsAtivos = !_inputsAtivos; AtualizarCorFundo(); return; }
            if (!_inputsAtivos) return;

            if (vkCode == 0x09)
            {
                if (_slotSobMouse != null && _slotSobMouse.MudarImagemAction != null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _slotSobMouse.MudarImagemAction.Invoke();
                    });
                    return;
                }
            }

            long agora = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            lock (matrizSlots)
            {
                foreach (var linha in matrizSlots) foreach (var slot in linha)
                {
                    if (slot.AtalhoKey == vkCode)
                    {
                        bool modObrigatorioOk = true;
                        if (slot.AtalhoMod != 0 && !IsKeyDown(slot.AtalhoMod)) modObrigatorioOk = false;
                        if (modObrigatorioOk)
                        {
                            if (!slot.KeyEstaPressionada)
                            {
                                int valorParaSomar = 1;
                                bool shiftPressionado = IsKeyDown(0x10);
                                if (shiftPressionado && slot.AtalhoMod != 0xA0 && slot.AtalhoMod != 0xA1 && slot.AtalhoMod != 0x10) valorParaSomar = -1;
                                slot.KeyEstaPressionada = true; slot.TempoInicioPress = agora; slot.ModoSubtracaoAtivo = false;
                                Modificar(slot, valorParaSomar);
                            }
                        }
                    }
                }
            }
        }

        private void ProcessarTeclaSolta(int vkCode)
        {
            lock (matrizSlots) { foreach (var linha in matrizSlots) foreach (var slot in linha) if (slot.AtalhoKey == vkCode) { slot.KeyEstaPressionada = false; slot.ModoSubtracaoAtivo = false; } }
        }

        private void InputLoop()
        {
            while (_appRodando)
            {
                long agora = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                if (_inputsAtivos)
                {
                    if (!_controleConectado || _joystick == null) { ProcurarControle(); }
                    else
                    {
                        try
                        {
                            _joystick.Poll(); var state = _joystick.GetCurrentState(); bool[] botoes = state.Buttons;
                            lock (matrizSlots)
                            {
                                foreach (var linha in matrizSlots) foreach (var slot in linha)
                                {
                                    if (slot.JoystickBtn != -1 && slot.JoystickBtn < botoes.Length)
                                    {
                                        if (botoes[slot.JoystickBtn])
                                        {
                                            if (!slot.PadEstaPressionado) { slot.PadEstaPressionado = true; slot.TempoInicioPress = agora; slot.ModoSubtracaoAtivo = false; Modificar(slot, 1); }
                                        }
                                        else { slot.PadEstaPressionado = false; slot.ModoSubtracaoAtivo = false; }
                                    }
                                }
                            }
                        }
                        catch { _joystick = null; _controleConectado = false; }
                    }
                    lock (matrizSlots)
                    {
                        foreach (var linha in matrizSlots) foreach (var slot in linha)
                        {
                            if (slot.KeyEstaPressionada || slot.PadEstaPressionado)
                            {
                                long tempoSegurando = agora - slot.TempoInicioPress;
                                if (!slot.ModoSubtracaoAtivo) { if (tempoSegurando > 400) { slot.ModoSubtracaoAtivo = true; Modificar(slot, -2); slot.UltimaRepeticao = agora; } }
                                else { if (agora - slot.UltimaRepeticao > 100) { Modificar(slot, -1); slot.UltimaRepeticao = agora; } }
                            }
                        }
                    }
                }
                Thread.Sleep(5);
            }
        }

        private void ProcurarControle()
        {
            try
            {
                if (_joystick != null) { try { _joystick.Poll(); return; } catch { _joystick.Dispose(); _joystick = null; _controleConectado = false; } }
                var deviceInstance = _directInput!.GetDevices(DeviceType.Gamepad, DeviceEnumerationFlags.AllDevices).FirstOrDefault() ?? _directInput!.GetDevices(DeviceType.Joystick, DeviceEnumerationFlags.AllDevices).FirstOrDefault();
                if (deviceInstance != null) { _joystick = new Joystick(_directInput, deviceInstance.InstanceGuid); _joystick.Acquire(); _controleConectado = true; } else { _controleConectado = false; }
            }
            catch { Thread.Sleep(1000); }
        }

        private void AtualizarCorFundo()
        {
            this.Dispatcher.BeginInvoke(() => { MainBackground.Background = _inputsAtivos ? new SolidColorBrush(Color.FromRgb(0, 255, 0)) : new SolidColorBrush(Color.FromRgb(255, 60, 60)); });
        }
        private static bool IsKeyDown(int vkCode) => (GetAsyncKeyState(vkCode) & 0x8000) != 0;
        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule!) { return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0); }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e); _hookID = SetHook(_proc);

            bool carregouSucesso = CarregarArquivo(autoSavePath);
            if (!carregouSucesso) LimparTudo();

            if (!carregouSucesso || !_jaViuAjuda)
            {
                this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    BtnDuvidas_Click(null, null);
                    _jaViuAjuda = true;
                    SalvarArquivo(autoSavePath, false);
                }), DispatcherPriority.ApplicationIdle);
            }
        }

        private void Janela_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is TextBlock) return;
            if (e.LeftButton == MouseButtonState.Pressed) try { DragMove(); } catch { }
        }

        private void Modificar(SlotConfig s, int v)
        {
            this.Dispatcher.BeginInvoke(() => {
                int a = int.Parse(s.ContadorTexto.Text);
                s.ContadorTexto.Text = Math.Max(0, a + v).ToString();
            });
        }

        // --- MENUS ---
        private void BtnLang_Click(object sender, MouseButtonEventArgs e) { isEnglish = !isEnglish; AtualizarTextosUI(); }
        private void AtualizarTextosUI()
        {
            if (isEnglish)
            {
                TxtLayout.Text = "LAYOUT ▼"; TxtCards.Text = "CARDS ▼";
                TxtExtra.Text = "EXTRAS ▼";
                TxtRank.Text = "• RANK CALC"; TxtPassword.Text = "• PASSWORD";
                TxtAtalhos.Text = "KEYS"; TxtDuvidas.Text = "HELP"; TxtSair.Text = "EXIT"; TxtLang.Text = "PT";
            }
            else
            {
                TxtLayout.Text = "LAYOUT ▼"; TxtCards.Text = "CARTAS ▼";
                TxtExtra.Text = "EXTRAS ▼";
                TxtRank.Text = "• RANK CALC"; TxtPassword.Text = "• PASSWORD";
                TxtAtalhos.Text = "TECLAS"; TxtDuvidas.Text = "DUVIDAS"; TxtSair.Text = "SAIR"; TxtLang.Text = "EN";
            }
        }

        private void BtnExtra_Click(object sender, MouseButtonEventArgs e)
        {
            if (PanelExtra.Visibility == Visibility.Visible) PanelExtra.Visibility = Visibility.Collapsed;
            else PanelExtra.Visibility = Visibility.Visible;
        }

        private void BtnDuvidas_Click(object? sender, MouseButtonEventArgs? e)
        {
            string pixKey = "fcb2fa70-22d3-4ac5-8d1e-cf1a4c470c38";
            string titulo = isEnglish ? "Help / How to Use" : "Dúvidas / Como Usar";
            string mensagem = isEnglish ?
                "CONTROLS SUMMARY:\n\n• Add (+1): Left Click (Tap) OR Configured Key.\n• Remove (-1): Right Click (Tap) OR Shift + Key.\n• Change Image: Hover mouse + Press TAB.\n• Pause Tracker: Configurable Key (Screen turns Red).\n\nMENU OPTIONS:\n• CARDS: Add/Remove Rows and Columns.\n• EXTRAS: Rank Calculator and Password Shop.\n• KEYS: Configure Keyboard or Controller.\n\n------------------------------\nSUPPORT THE PROJECT (PIX):\nIf you liked the tool, consider supporting!\n\nKey: " + pixKey
                :
                "RESUMO DOS CONTROLES:\n\n• Adicionar (+1): Clique Esquerdo (Toque) OU Tecla.\n• Remover (-1): Clique Direito (Toque) OU Shift + Tecla.\n• Trocar Imagem: Passe o mouse em cima + Aperte TAB.\n• Pausar Tracker: Tecla configurável (Tela fica Vermelha).\n\nOPÇÕES DE MENU:\n• CARTAS: Adicionar/Remover Linhas e Colunas.\n• EXTRAS: Calculadora de Rank e Loja de Senhas.\n• TECLAS: Configurar Teclado ou Controle.\n\n------------------------------\nAPOIE O PROJETO (PIX):\nSe gostou da ferramenta e quiser ajudar:\n\nChave: " + pixKey;

            if (MessageBox.Show(mensagem + "\n\n(Click OK to copy PIX)", titulo, MessageBoxButton.OKCancel, MessageBoxImage.Information) == MessageBoxResult.OK) { try { Clipboard.SetText(pixKey); } catch { } }
        }

        private void BtnLayout_Click(object sender, MouseButtonEventArgs e)
        {
            ContextMenu menu = new ContextMenu();
            string txtSalvar = isEnglish ? "Save Layout As..." : "Salvar Layout Como...";
            string txtCarregar = isEnglish ? "Load Layout..." : "Carregar Layout...";
            string filter = isEnglish ? "JSON Files|*.json" : "Arquivos JSON|*.json";
            MenuItem itemSalvar = new MenuItem { Header = txtSalvar };
            itemSalvar.Click += (s, args) => { SaveFileDialog d = new SaveFileDialog { Filter = filter, FileName = "Layout.json" }; if (d.ShowDialog() == true) SalvarArquivo(d.FileName, true); };
            MenuItem itemCarregar = new MenuItem { Header = txtCarregar };
            itemCarregar.Click += (s, args) => { OpenFileDialog d = new OpenFileDialog { Filter = filter }; if (d.ShowDialog() == true) CarregarArquivo(d.FileName); };
            menu.Items.Add(itemSalvar); menu.Items.Add(itemCarregar); menu.IsOpen = true;
        }
        private void BtnCards_Click(object sender, MouseButtonEventArgs e)
        {
            ContextMenu menu = new ContextMenu();
            string txtLinhaAdd = isEnglish ? "Add Row (+)" : "Adicionar Linha (+)";
            string txtColAdd = isEnglish ? "Add Column (+)" : "Adicionar Coluna (+)";
            string txtLinhaRem = isEnglish ? "Remove Row (-)" : "Remover Linha (-)";
            string txtColRem = isEnglish ? "Remove Column (-)" : "Remover Coluna (-)";
            string txtZerar = isEnglish ? "Reset Counters (0)" : "Zerar Contadores (0)";
            string txtReset = isEnglish ? "Reset App (Default)" : "Reiniciar App (Padrão)";
            MenuItem itemLinha = new MenuItem { Header = txtLinhaAdd }; itemLinha.Click += (s, args) => AdicionarLinha();
            MenuItem itemCol = new MenuItem { Header = txtColAdd }; itemCol.Click += (s, args) => AdicionarColuna();
            MenuItem itemLinhaR = new MenuItem { Header = txtLinhaRem }; itemLinhaR.Click += (s, args) => RemoverLinha();
            MenuItem itemColR = new MenuItem { Header = txtColRem }; itemColR.Click += (s, args) => RemoverColuna();
            MenuItem itemZerar = new MenuItem { Header = txtZerar, Foreground = Brushes.Magenta };
            itemZerar.Click += (s, args) => { lock (matrizSlots) foreach (var linha in matrizSlots) foreach (var slot in linha) slot.ContadorTexto.Text = "0"; };
            MenuItem itemReset = new MenuItem { Header = txtReset, Foreground = Brushes.Red, FontWeight = FontWeights.Bold };
            itemReset.Click += (s, args) => { if (MessageBox.Show("Confirm?", "Reset", MessageBoxButton.YesNo) == MessageBoxResult.Yes) LimparTudo(); };
            menu.Items.Add(itemLinha); menu.Items.Add(itemLinhaR); menu.Items.Add(new Separator());
            menu.Items.Add(itemCol); menu.Items.Add(itemColR); menu.Items.Add(new Separator());
            menu.Items.Add(itemZerar); menu.Items.Add(new Separator()); menu.Items.Add(itemReset);
            menu.IsOpen = true;
        }
        private void BtnAtalhos_Click(object sender, MouseButtonEventArgs e) => AbrirConfiguracaoTeclas();
        private void BtnSair_Click(object sender, MouseButtonEventArgs e) => Application.Current.Shutdown();

        private void BtnHundo_Click(object sender, MouseButtonEventArgs e) => new HundoWindow(isEnglish).Show();
        private void BtnRank_Click(object sender, MouseButtonEventArgs e) => new RankCalculatorWindow(isEnglish).Show();
        private void BtnPassword_Click(object sender, MouseButtonEventArgs e) => new PasswordShopWindow(isEnglish).Show();


        private void AdicionarLinha(object? sender = null, RoutedEventArgs? e = null)
        {
            lock (matrizSlots)
            {
                var margem = (matrizSlots.Count > 0) ? new Thickness(0, -1, 0, 0) : new Thickness(0);
                StackPanel novaLinhaUI = new StackPanel { Orientation = Orientation.Horizontal, Margin = margem };
                List<SlotConfig> novaLinhaLogica = new List<SlotConfig>();
                int colunasParaAdicionar = (matrizSlots.Count == 0) ? 1 : Math.Max(1, colunasAtuais);
                for (int i = 0; i < colunasParaAdicionar; i++) { var s = CriarSlotUI(); novaLinhaUI.Children.Add(s.Container); novaLinhaLogica.Add(s.Config); }
                matrizSlots.Add(novaLinhaLogica); LinhasContainer.Children.Add(novaLinhaUI);
                colunasAtuais = colunasParaAdicionar;
            }
        }
        private void AdicionarColuna(object? sender = null, RoutedEventArgs? e = null)
        {
            lock (matrizSlots)
            {
                if (matrizSlots.Count == 0) AdicionarLinha();
                else
                {
                    colunasAtuais++;
                    for (int i = 0; i < matrizSlots.Count; i++) { var s = CriarSlotUI(); matrizSlots[i].Add(s.Config); (LinhasContainer.Children[i] as StackPanel)!.Children.Add(s.Container); }
                }
            }
        }
        private void RemoverLinha()
        {
            lock (matrizSlots) { if (matrizSlots.Count > 1) { int lastIndex = matrizSlots.Count - 1; matrizSlots.RemoveAt(lastIndex); LinhasContainer.Children.RemoveAt(lastIndex); } }
        }
        private void RemoverColuna()
        {
            lock (matrizSlots) { if (colunasAtuais > 1) { colunasAtuais--; foreach (var linha in matrizSlots) { if (linha.Count > 0) linha.RemoveAt(linha.Count - 1); } foreach (StackPanel painelLinha in LinhasContainer.Children) { if (painelLinha.Children.Count > 0) painelLinha.Children.RemoveAt(painelLinha.Children.Count - 1); } } }
        }

        private (Grid Container, SlotConfig Config) CriarSlotUI()
        {
            var config = new SlotConfig();
            Grid container = new Grid { Width = 96, Height = 96, Background = Brushes.Transparent, Margin = new Thickness(0) };
            config.ImagemCarta = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
            config.ContadorTexto = new TextBlock { Text = "0", FontSize = 22, Foreground = Brushes.White, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Border b = new Border { Background = Brushes.Black, Width = 35, Height = 35, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1, 1, 0, 0) };
            b.Child = config.ContadorTexto;

            Action AbrirImagem = () =>
            {
                string filter = isEnglish ? "Images|*.jpg;*.jpeg;*.png" : "Imagens|*.jpg;*.jpeg;*.png";
                OpenFileDialog op = new OpenFileDialog { Filter = filter };

                // Tenta abrir a janela direto na pasta "icons" para facilitar
                string pastaIcons = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons");
                if (Directory.Exists(pastaIcons)) op.InitialDirectory = pastaIcons;

                if (op.ShowDialog() == true)
                {
                    // SALVA APENAS O NOME DO ARQUIVO (ex: "009 - Shadow Specter.png") no JSON
                    config.ImagemAtualPath = Path.GetFileName(op.FileName);

                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.UriSource = new Uri(op.FileName); // Usa o caminho absoluto temporariamente só para mostrar na tela agora
                        bi.EndInit();
                        config.ImagemCarta.Source = bi;
                    }
                    catch { }
                }
            };
            config.MudarImagemAction = AbrirImagem;

            container.MouseEnter += (s, ev) => { _slotSobMouse = config; };
            container.MouseLeave += (s, ev) => { if (_slotSobMouse == config) _slotSobMouse = null; };

            container.MouseLeftButtonDown += (s, ev) => { ev.Handled = true; };
            container.MouseLeftButtonUp += (s, ev) => { ev.Handled = true; Modificar(config, 1); };

            container.MouseRightButtonDown += (s, ev) => { ev.Handled = true; };
            container.MouseRightButtonUp += (s, ev) => { ev.Handled = true; Modificar(config, -1); };

            Grid interno = new Grid(); interno.Children.Add(new TextBlock { Text = "+", Foreground = Brushes.White, Opacity = 0.2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            interno.Children.Add(config.ImagemCarta); interno.Children.Add(b);
            Border moldura = new Border { Background = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), ClipToBounds = true, Child = interno };
            container.Children.Add(moldura);
            return (container, config);
        }

        public void AbrirConfiguracaoTeclas(object? sender = null, RoutedEventArgs? e = null)
        {
            string title = isEnglish ? "Input Configuration" : "Configurar Controles";
            string help = isEnglish ? "Press any KEY to set.\n(Tip: SHIFT + Key removes a card)" : "Aperte uma TECLA para definir.\n(Dica: SHIFT + Tecla remove uma carta)";
            string wait = isEnglish ? "Press..." : "Aperte...";
            string toggleTxt = isEnglish ? "Set Pause Key (ON/OFF)" : "Definir Tecla de Pausa (ON/OFF)";
            Window win = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, Padding = new Thickness(15), WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true, ResizeMode = ResizeMode.NoResize };
            StackPanel stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = help, Margin = new Thickness(0, 0, 0, 10), FontWeight = FontWeights.Bold, TextAlignment = WpfTextAlignment.Center });
            KeyEventHandler? keyHandler = null; DispatcherTimer? padPoller = null;
            void LimparHandlers() { if (keyHandler != null) { win.KeyDown -= keyHandler; keyHandler = null; } if (padPoller != null) { padPoller.Stop(); padPoller = null; } }
            string GetKeyName(int vk, int mod) { if (vk == 0) return ""; string m = ((WpfKey)KeyInterop.KeyFromVirtualKey(vk)).ToString(); return mod != 0 ? $"{((WpfKey)KeyInterop.KeyFromVirtualKey(mod))} + {m}" : m; }
            string GetBtnName(int btnIndex) { if (btnIndex == -1) return ""; return "🎮 " + btnIndex; }
            int DetectarModificador(int ignoreKey) { for (int i = 1; i < 190; i++) { if (i == ignoreKey) continue; if (IsKeyDown(i)) return i; } return 0; }
            Button btnPausa = new Button { Content = $"{toggleTxt}: {GetKeyName(_teclaPausa, 0)}", Background = Brushes.LightGray, Padding = new Thickness(5), Margin = new Thickness(0, 0, 0, 15) };
            btnPausa.Click += (se, ev) =>
            {
                ev.Handled = true; LimparHandlers(); btnPausa.Content = wait; btnPausa.Background = Brushes.Yellow;
                keyHandler = (sk, ex) => { ex.Handled = true; _teclaPausa = KeyInterop.VirtualKeyFromKey(ex.Key); btnPausa.Content = $"{toggleTxt}: {GetKeyName(_teclaPausa, 0)}"; btnPausa.ClearValue(Button.BackgroundProperty); SalvarArquivo(autoSavePath, false); LimparHandlers(); };
                win.KeyDown += keyHandler;
            };
            stack.Children.Add(btnPausa); stack.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 10) });
            for (int l = 0; l < matrizSlots.Count; l++)
            {
                for (int c = 0; c < matrizSlots[l].Count; c++)
                {
                    var s = matrizSlots[l][c];
                    StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                    row.Children.Add(new TextBlock { Text = $"L{l + 1}C{c + 1}: ", Width = 55, VerticalAlignment = VerticalAlignment.Center });
                    string label = "Definir";
                    if (s.AtalhoKey != 0) label = GetKeyName(s.AtalhoKey, s.AtalhoMod); else if (s.JoystickBtn != -1) label = GetBtnName(s.JoystickBtn);
                    Button btn = new Button { Content = label, Width = 150 };
                    btn.Click += (se, ev) =>
                    {
                        ev.Handled = true; LimparHandlers(); btn.Content = wait; btn.Background = Brushes.Yellow;
                        keyHandler = (sk, ex) => { ex.Handled = true; int vkPrincipal = KeyInterop.VirtualKeyFromKey(ex.Key); int vkMod = DetectarModificador(vkPrincipal); s.AtalhoKey = vkPrincipal; s.AtalhoMod = vkMod; s.JoystickBtn = -1; btn.Content = GetKeyName(s.AtalhoKey, s.AtalhoMod); btn.ClearValue(Button.BackgroundProperty); LimparHandlers(); SalvarArquivo(autoSavePath, false); };
                        win.KeyDown += keyHandler;
                        padPoller = new DispatcherTimer(); padPoller.Interval = TimeSpan.FromMilliseconds(50);
                        padPoller.Tick += (senderTimer, argsTimer) => { if (_joystick != null) { try { _joystick.Poll(); var state = _joystick.GetCurrentState(); bool[] btns = state.Buttons; for (int i = 0; i < btns.Length; i++) { if (btns[i]) { s.JoystickBtn = i; s.AtalhoKey = 0; s.AtalhoMod = 0; btn.Content = GetBtnName(s.JoystickBtn); btn.ClearValue(Button.BackgroundProperty); LimparHandlers(); SalvarArquivo(autoSavePath, false); break; } } } catch { } } };
                        padPoller.Start();
                    };
                    row.Children.Add(btn); stack.Children.Add(row);
                }
            }
            win.Closed += (s, ev) => LimparHandlers();
            win.Content = new ScrollViewer { Content = stack, MaxHeight = 400 };
            win.ShowDialog();
        }

        private void SalvarArquivo(string caminho, bool mostrarMsg)
        {
            try
            {
                lock (matrizSlots)
                {
                    var root = new RootSaveData();
                    root.PauseKey = _teclaPausa;
                    root.WindowLeft = this.Left;
                    root.WindowTop = this.Top;
                    root.HasSeenHelp = _jaViuAjuda;
                    for (int l = 0; l < matrizSlots.Count; l++) for (int c = 0; c < matrizSlots[l].Count; c++) { var s = matrizSlots[l][c]; root.Slots.Add(new SlotData { Linha = l, Coluna = c, ImagemPath = s.ImagemAtualPath, AtalhoKey = s.AtalhoKey, AtalhoMod = s.AtalhoMod, JoystickBtn = s.JoystickBtn, Valor = int.Parse(s.ContadorTexto.Text) }); }
                    File.WriteAllText(caminho, JsonSerializer.Serialize(root));
                }
                if (mostrarMsg) MessageBox.Show(isEnglish ? "Saved!" : "Salvo!");
            }
            catch { }
        }
        private bool CarregarArquivo(string caminho)
        {
            if (!File.Exists(caminho)) return false;
            try
            {
                string json = File.ReadAllText(caminho);
                try
                {
                    var root = JsonSerializer.Deserialize<RootSaveData>(json);
                    if (root != null)
                    {
                        _teclaPausa = root.PauseKey;
                        this.Left = root.WindowLeft;
                        this.Top = root.WindowTop;
                        _jaViuAjuda = root.HasSeenHelp;
                        if (root.Slots != null) CarregarSlots(root.Slots);
                        return true;
                    }
                }
                catch
                {
                    var oldList = JsonSerializer.Deserialize<List<SlotData>>(json);
                    if (oldList != null) { CarregarSlots(oldList); return true; }
                }
                return false;
            }
            catch { return false; }
        }
        private void CarregarSlots(List<SlotData> dados)
        {
            lock (matrizSlots)
            {
                matrizSlots.Clear(); LinhasContainer.Children.Clear(); colunasAtuais = 0;
                if (dados.Count > 0)
                {
                    int maxL = dados.Max(d => d.Linha) + 1; int maxC = dados.Max(d => d.Coluna) + 1;
                    for (int i = 0; i < maxL; i++) AdicionarLinha(); while (colunasAtuais < maxC) AdicionarColuna();
                    foreach (var d in dados)
                    {
                        if (d.Linha < matrizSlots.Count && d.Coluna < matrizSlots[d.Linha].Count)
                        {
                            var s = matrizSlots[d.Linha][d.Coluna];
                            s.AtalhoKey = d.AtalhoKey;
                            s.AtalhoMod = d.AtalhoMod;
                            s.JoystickBtn = d.JoystickBtn;
                            s.ContadorTexto.Text = d.Valor.ToString();

                            // Extrai APENAS o nome do arquivo (limpa caminhos antigos se existirem)
                            string nomeArquivo = Path.GetFileName(d.ImagemPath);
                            s.ImagemAtualPath = nomeArquivo; // Mantém no slot só o nome

                            // Constrói o caminho buscando obrigatoriamente na pasta "icons"
                            string caminhoIcons = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", nomeArquivo);
                            string caminhoFinal = "";

                            if (File.Exists(caminhoIcons))
                            {
                                caminhoFinal = caminhoIcons;
                            }
                            else if (File.Exists(d.ImagemPath))
                            { // Fallback de emergência
                                caminhoFinal = d.ImagemPath;
                            }

                            if (!string.IsNullOrEmpty(caminhoFinal) && File.Exists(caminhoFinal))
                            {
                                try
                                {
                                    var bi = new BitmapImage();
                                    bi.BeginInit();
                                    bi.CacheOption = BitmapCacheOption.OnLoad;
                                    bi.UriSource = new Uri(caminhoFinal, UriKind.Absolute);
                                    bi.EndInit();
                                    s.ImagemCarta.Source = bi;
                                }
                                catch { }
                            }
                        }
                    }
                }
                else { AdicionarLinha(); }
            }
        }
        private void LimparTudo(object? sender = null, RoutedEventArgs? e = null)
        {
            lock (matrizSlots) { matrizSlots.Clear(); LinhasContainer.Children.Clear(); colunasAtuais = 0; AdicionarLinha(); }
        }
    }

    public class RankCalculatorWindow : Window
    {
        private Dictionary<string, int> counters = new Dictionary<string, int>();
        private Dictionary<string, TextBlock> uiReferences = new Dictionary<string, TextBlock>();

        private TextBlock scoreTxt = null!;
        private TextBlock rankTxt = null!;
        private bool isEnglish;

        public RankCalculatorWindow(bool isEng)
        {
            isEnglish = isEng;
            Title = "Rank Calculator";
            SizeToContent = SizeToContent.WidthAndHeight; // Ajusta o tamanho da janela automaticamente
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            StackPanel mainStack = new StackPanel { Margin = new Thickness(10) };

            // --- CABEÇALHO: SCORE E RESET ---
            Border scoreBorder = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10) };
            StackPanel scorePanel = new StackPanel();
            scoreTxt = new TextBlock { Text = "SCORE: 50", Foreground = Brushes.Yellow, FontSize = 24, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
            rankTxt = new TextBlock { Text = "D-POW", Foreground = Brushes.White, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center };
            scorePanel.Children.Add(scoreTxt); scorePanel.Children.Add(rankTxt); scoreBorder.Child = scorePanel;
            mainStack.Children.Add(scoreBorder);

            string resetText = isEnglish ? "RESET" : "REINICIAR";
            Button btnReset = new Button
            {
                Content = resetText,
                Background = Brushes.OrangeRed,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Height = 30,
                Margin = new Thickness(0, 0, 0, 10)
            };
            btnReset.Click += (s, e) => ResetAll();
            mainStack.Children.Add(btnReset);

            // --- INICIALIZAR VALORES OCULTOS (Necessário para a matemática do Score não quebrar) ---
            counters["Turns"] = 0;
            counters["Def. Wins"] = 0;
            counters["Cards Used"] = 0;
            counters["LP (x100)"] = 80;

            // --- BLOCOS DE IMAGENS HORIZONTAIS ---
            StackPanel imagesPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

            // Adiciona as cartas solicitadas (Nome Lógico, Nome do Arquivo)
            AddImageBlock(imagesPanel, "Eff. Attacks", "efetivo");
            AddImageBlock(imagesPanel, "Fusion", "fusão");
            AddImageBlock(imagesPanel, "Equip Magic", "equipamento");
            AddImageBlock(imagesPanel, "Triggers", "armadilha");
            AddImageBlock(imagesPanel, "Face-Down", "facedown");
            AddImageBlock(imagesPanel, "Pure Magic", "magia");

            mainStack.Children.Add(imagesPanel);

            Content = mainStack;
            UpdateScore();
        }

        private void AddImageBlock(StackPanel parent, string logicName, string fileNameBase)
        {
            counters[logicName] = 0;

            // Container principal para a imagem e botões (sem margens para ficarem grudados)
            StackPanel block = new StackPanel { Margin = new Thickness(0) };

            // Grid para sobrepor o contador em cima da imagem
            Grid imgGrid = new Grid { Width = 96, Height = 96 };

            Image img = new Image { Stretch = Stretch.Fill };

            // Tenta carregar a imagem (.png ou .jpg) da pasta icons
            string pathPng = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", fileNameBase + ".png");
            string pathJpg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", fileNameBase + ".jpg");
            string finalPath = File.Exists(pathPng) ? pathPng : (File.Exists(pathJpg) ? pathJpg : "");

            if (!string.IsNullOrEmpty(finalPath))
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(finalPath, UriKind.Absolute);
                    bi.EndInit();
                    img.Source = bi;
                }
                catch { }
            }
            imgGrid.Children.Add(img);

            // Contador no canto inferior direito
            Border counterBorder = new Border
            {
                Background = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Padding = new Thickness(4, 2, 4, 2),
                BorderThickness = new Thickness(1, 1, 0, 0),
                BorderBrush = Brushes.Gray
            };
            TextBlock valTxt = new TextBlock { Text = "0", Foreground = Brushes.Cyan, FontWeight = FontWeights.Bold, FontSize = 14 };
            counterBorder.Child = valTxt;
            imgGrid.Children.Add(counterBorder);
            uiReferences[logicName] = valTxt;

            block.Children.Add(imgGrid);

            // Botões de + e - embaixo da imagem
            Grid btnGrid = new Grid { Height = 25 };
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Button btnPlus = new Button { Content = "+", Background = Brushes.Green, Foreground = Brushes.White, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0.5), BorderBrush = Brushes.Black };
            btnPlus.Click += (s, e) => { counters[logicName]++; valTxt.Text = counters[logicName].ToString(); UpdateScore(); };

            Button btnMinus = new Button { Content = "-", Background = Brushes.Red, Foreground = Brushes.White, FontWeight = FontWeights.Bold, BorderThickness = new Thickness(0.5), BorderBrush = Brushes.Black };
            btnMinus.Click += (s, e) => { counters[logicName] = Math.Max(0, counters[logicName] - 1); valTxt.Text = counters[logicName].ToString(); UpdateScore(); };

            Grid.SetColumn(btnPlus, 0);
            Grid.SetColumn(btnMinus, 1);
            btnGrid.Children.Add(btnPlus);
            btnGrid.Children.Add(btnMinus);

            block.Children.Add(btnGrid);

            // Adiciona o bloco inteiro na linha horizontal principal
            parent.Children.Add(block);
        }

        private void ResetAll()
        {
            var keys = new List<string>(counters.Keys);
            foreach (var k in keys)
            {
                if (k == "LP (x100)")
                {
                    counters[k] = 80;
                }
                else
                {
                    counters[k] = 0;
                    if (uiReferences.ContainsKey(k)) uiReferences[k].Text = "0";
                }
            }
            UpdateScore();
        }

        private void UpdateScore()
        {
            int score = 50;
            int t = counters["Turns"]; if (t <= 4) score += 12; else if (t <= 8) score += 8; else if (t > 28) { if (t <= 32) score -= 8; else score -= 12; }
            int atk = counters["Eff. Attacks"]; if (atk <= 1) score += 4; else if (atk <= 3) score += 2; else if (atk > 9) { if (atk <= 19) score -= 2; else score -= 4; }
            int def = counters["Def. Wins"]; if (def > 1) { if (def <= 5) score -= 10; else if (def <= 9) score -= 20; else if (def <= 14) score -= 30; else score -= 40; }
            int fd = counters["Face-Down"]; if (fd > 0) { if (fd <= 10) score -= 2; else if (fd <= 20) score -= 4; else if (fd <= 30) score -= 6; else score -= 8; }
            int fus = counters["Fusion"]; if (fus == 0) score += 4; else if (fus > 4) { if (fus <= 9) score -= 4; else if (fus <= 14) score -= 8; else score -= 12; }
            int eq = counters["Equip Magic"]; if (eq == 0) score += 4; else if (eq > 4) { if (eq <= 9) score -= 4; else if (eq <= 14) score -= 8; else score -= 12; }
            int mag = counters["Pure Magic"]; if (mag == 0) score += 2; else if (mag <= 3) score -= 4; else if (mag <= 6) score -= 8; else if (mag <= 9) score -= 12; else score -= 16;
            int tr = counters["Triggers"]; if (tr == 0) score += 2; else if (tr <= 2) score -= 8; else if (tr <= 4) score -= 16; else if (tr <= 6) score -= 24; else score -= 32;
            int cu = counters["Cards Used"]; if (cu <= 8) score += 15; else if (cu <= 12) score += 12; else if (cu > 32) { if (cu <= 36) score -= 5; else score -= 7; }
            int lp = counters["LP (x100)"] * 100; if (lp < 1000) score -= 7; else if (lp < 7000) score += 0; else if (lp < 8000) score += 4; else score += 6;
            score = Math.Max(0, Math.Min(99, score));
            scoreTxt.Text = "SCORE: " + score;
            string r = "D-POW";
            if (score >= 90) r = "S-POW";
            else if (score >= 80) r = "A-POW";
            else if (score >= 70) r = "B-POW";
            else if (score >= 60) r = "C-POW";
            else if (score >= 50) r = "D-POW";
            else if (score >= 40) r = "D-TEC"; else if (score >= 30) r = "C-TEC"; else if (score >= 20) r = "B-TEC"; else if (score >= 10) r = "A-TEC"; else r = "S-TEC";
            rankTxt.Text = r;
            if (r.Contains("S-POW")) rankTxt.Foreground = Brushes.Gold; else if (r.Contains("S-TEC")) rankTxt.Foreground = Brushes.Cyan; else rankTxt.Foreground = Brushes.White;
        }
    }

    public class PasswordShopWindow : Window
    {
        public class CardInfo { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public int Cost { get; set; } public string Pw { get; set; } = ""; }
        private List<CardInfo> allCards = new List<CardInfo>();
        private int currentStarChips = 0;
        private TextBlock scDisplay = null!;
        private StackPanel listPanel = null!;

        public PasswordShopWindow(bool isEng)
        {
            // LISTA RESTRITA (COM IDs PARA IMAGENS)
            allCards.Add(new CardInfo { Id = "191", Cost = 25, Name = "LaLa Li-oon", Pw = "09430387" });
            allCards.Add(new CardInfo { Id = "122", Cost = 25, Name = "Yamatano Dragon Scroll", Pw = "76704943" });
            allCards.Add(new CardInfo { Id = "537", Cost = 30, Name = "Mega Thunderball", Pw = "21817254" });
            allCards.Add(new CardInfo { Id = "045", Cost = 30, Name = "Oscillo Hero", Pw = "27324313" });
            allCards.Add(new CardInfo { Id = "211", Cost = 30, Name = "Kaminarikozou", Pw = "15510988" });
            allCards.Add(new CardInfo { Id = "200", Cost = 30, Name = "Petit Dragon", Pw = "75356564" });
            allCards.Add(new CardInfo { Id = "610", Cost = 35, Name = "Electric Lizard", Pw = "55875323" });
            allCards.Add(new CardInfo { Id = "334", Cost = 55, Name = "Umi", Pw = "22702055" });

            // NOVOS
            allCards.Add(new CardInfo { Id = "463", Cost = 40, Name = "Electric Snake", Pw = "11324436" });
            allCards.Add(new CardInfo { Id = "461", Cost = 50, Name = "Bolt Penguin", Pw = "48531733" });
            allCards.Add(new CardInfo { Id = "298", Cost = 45, Name = "Wicked Dragon with the Ersatz Head", Pw = "02957055" });

            Title = "Password Shop"; Width = 350; Height = 500;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            DockPanel mainDock = new DockPanel { Margin = new Thickness(10) };

            Border headerBorder = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10) };
            StackPanel headerStack = new StackPanel();

            // --- ALTERAÇÃO: Criando o painel horizontal com a Estrela e o Número ---
            StackPanel scPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };

            Image starIcon = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0) };
            string starPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", "estrela.png");
            if (File.Exists(starPath))
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(starPath, UriKind.Absolute);
                    bi.EndInit();
                    starIcon.Source = bi;
                }
                catch { }
            }

            scDisplay = new TextBlock { Text = "0", Foreground = Brushes.Gold, FontSize = 24, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };

            scPanel.Children.Add(starIcon);
            scPanel.Children.Add(scDisplay);
            headerStack.Children.Add(scPanel);
            // ------------------------------------------------------------------------

            Grid btnGrid = new Grid();
            for (int i = 0; i < 5; i++) btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Button btnS = CreateRankBtn("S", Brushes.Gold, 5); Grid.SetColumn(btnS, 0); btnGrid.Children.Add(btnS);
            Button btnA = CreateRankBtn("A", Brushes.Silver, 4); Grid.SetColumn(btnA, 1); btnGrid.Children.Add(btnA);
            Button btnB = CreateRankBtn("B", Brushes.OrangeRed, 3); Grid.SetColumn(btnB, 2); btnGrid.Children.Add(btnB);
            Button btnC = CreateRankBtn("C", Brushes.CornflowerBlue, 2); Grid.SetColumn(btnC, 3); btnGrid.Children.Add(btnC);
            Button btnD = CreateRankBtn("D", Brushes.Gray, 1); Grid.SetColumn(btnD, 4); btnGrid.Children.Add(btnD);

            headerStack.Children.Add(btnGrid);
            headerBorder.Child = headerStack;
            DockPanel.SetDock(headerBorder, Dock.Top);
            mainDock.Children.Add(headerBorder);

            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            listPanel = new StackPanel();
            scroll.Content = listPanel;
            mainDock.Children.Add(scroll);

            Content = mainDock;
            UpdateList();
        }

        private Button CreateRankBtn(string txt, Brush color, int val)
        {
            Button b = new Button { Content = txt, Background = color, Foreground = Brushes.Black, FontWeight = FontWeights.Bold, Margin = new Thickness(2), Height = 30, FontSize = 16 };
            b.Click += (s, e) => { currentStarChips += val; UpdateList(); };
            return b;
        }

        private void UpdateList()
        {
            // --- ALTERAÇÃO: Removido o texto " StarChip" ---
            scDisplay.Text = $"{currentStarChips}";
            listPanel.Children.Clear();

            var affordable = allCards.Where(c => c.Cost <= currentStarChips).OrderByDescending(c => c.Cost).ToList();

            if (affordable.Count == 0)
            {
                listPanel.Children.Add(new TextBlock { Text = "Get Ranks (S-D) to earn StarChips...", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) });
            }
            else
            {
                foreach (var card in affordable)
                {
                    Border cardRow = new Border { Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)), Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(5), CornerRadius = new CornerRadius(3), Cursor = Cursors.Hand };

                    cardRow.MouseLeftButtonUp += (s, e) =>
                    {
                        if (currentStarChips >= card.Cost)
                        {
                            currentStarChips -= card.Cost;
                            // --- INTEGRAÇÃO COM EXCEL NA LOJA ---
                            MainWindow.SalvarCartaNoExcel(card.Id);
                            UpdateList();
                        }
                    };

                    Grid g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); // IMG
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) }); // COST
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // NAME
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) }); // PW

                    Image img = new Image { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };

                    string idToUse = !string.IsNullOrEmpty(card.Id) ? card.Id : card.Pw;

                    // SUPORTE PARA .png e .PNG (Maiúsculo/Minúsculo) e .jpg
                    string iconPathPng = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", idToUse + ".png");
                    string iconPathPngUpper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", idToUse + ".PNG");
                    string iconPathJpg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons", idToUse + ".jpg");

                    BitmapImage bi = new BitmapImage();
                    if (File.Exists(iconPathPng))
                    {
                        bi.BeginInit(); bi.UriSource = new Uri(iconPathPng, UriKind.Absolute); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); img.Source = bi;
                    }
                    else if (File.Exists(iconPathPngUpper))
                    {
                        bi.BeginInit(); bi.UriSource = new Uri(iconPathPngUpper, UriKind.Absolute); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); img.Source = bi;
                    }
                    else if (File.Exists(iconPathJpg))
                    {
                        bi.BeginInit(); bi.UriSource = new Uri(iconPathJpg, UriKind.Absolute); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); img.Source = bi;
                    }

                    TextBlock txtCost = new TextBlock { Text = $"{card.Cost}", Foreground = Brushes.Cyan, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                    TextBlock txtName = new TextBlock { Text = card.Name, Foreground = Brushes.White, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    TextBox txtPw = new TextBox { Text = card.Pw, Background = Brushes.Transparent, Foreground = Brushes.Yellow, BorderThickness = new Thickness(0), IsReadOnly = true, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };

                    Grid.SetColumn(img, 0); Grid.SetColumn(txtCost, 1); Grid.SetColumn(txtName, 2); Grid.SetColumn(txtPw, 3);
                    g.Children.Add(img); g.Children.Add(txtCost); g.Children.Add(txtName); g.Children.Add(txtPw);
                    cardRow.Child = g;
                    listPanel.Children.Add(cardRow);
                }
            }
        }
    }


    // 1. Classe CartaFaltante 
    public class CartaFaltante
    {
        public string Id { get; set; } = "";
        public string Nome { get; set; } = "";
        public string CaminhoImagem { get; set; } = "";
        public string DropFrom { get; set; } = "";
        public string Rank { get; set; } = "";
        public string Password { get; set; } = "";
        public string Starchip { get; set; } = "";
        public string Disponibilidade { get; set; } = "";
    }

    // 2. Classe para armazenar a estrutura das Fusões lidas do CSV
    public class FusaoInfo
    {
        public string Mat1 { get; set; } = "";
        public string Mat2 { get; set; } = "";
        public string Resultado { get; set; } = "";
        public string Extra { get; set; } = "";
    }


    // NOVA CLASSE: Para guardar os dados dos Rituais
    public class RitualInfo
    {
        public string Mat1 { get; set; } = "";
        public string Mat2 { get; set; } = "";
        public string Mat3 { get; set; } = "";
        public string Magica { get; set; } = "";
        public string Resultado { get; set; } = "";
    }

    // 3. A Janela de Detalhes da Carta ATUALIZADA (Com Feedback Visual de Cartas Obtidas/Faltantes)
    public class DetalhesCartaWindow : Window
    {
        private bool isEnglish;
        private Image imgPrincipal = null!;
        private StackPanel painelInfo = null!;
        private ScrollViewer scrollInfo = null!;

        public DetalhesCartaWindow(CartaFaltante carta, bool isEng)
        {
            isEnglish = isEng;
            Width = 650; Height = 550;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.NoResize;

            Grid principal = new Grid { Margin = new Thickness(15) };
            principal.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            principal.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border bordaImg = new Border
            {
                BorderBrush = Brushes.Gold,
                BorderThickness = new Thickness(3),
                Margin = new Thickness(0, 0, 15, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            imgPrincipal = new Image { Stretch = Stretch.Uniform, MaxHeight = 300 };
            bordaImg.Child = imgPrincipal;
            Grid.SetColumn(bordaImg, 0);
            principal.Children.Add(bordaImg);

            scrollInfo = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 10, 0) };
            painelInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            scrollInfo.Content = painelInfo;
            Grid.SetColumn(scrollInfo, 1);
            principal.Children.Add(scrollInfo);

            Content = principal;
            CarregarDadosCarta(carta);
        }

        private void CarregarDadosCarta(CartaFaltante carta)
        {
            Title = isEnglish ? $"Card Details - {carta.Nome}" : $"Detalhes da Carta - {carta.Nome}";
            scrollInfo.ScrollToTop();

            imgPrincipal.Source = null;
            try
            {
                var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(carta.CaminhoImagem, UriKind.Absolute); bi.EndInit();
                imgPrincipal.Source = bi;
            }
            catch { }

            painelInfo.Children.Clear();

            painelInfo.Children.Add(new TextBlock { Text = $"[{carta.Id}] {carta.Nome}", Foreground = Brushes.Gold, FontSize = 22, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 15) });

            painelInfo.Children.Add(new TextBlock { Text = isEnglish ? "⚔️ Drop From:" : "⚔️ Dropa de:", Foreground = Brushes.LightGray, FontSize = 14 });
            painelInfo.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(carta.DropFrom) ? "-" : carta.DropFrom, Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });

            painelInfo.Children.Add(new TextBlock { Text = isEnglish ? "🏆 Rank:" : "🏆 Rank:", Foreground = Brushes.LightGray, FontSize = 14 });
            painelInfo.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(carta.Rank) ? "-" : carta.Rank, Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });

            if (carta.Disponibilidade != "N" && !string.IsNullOrEmpty(carta.Password))
            {
                painelInfo.Children.Add(new TextBlock { Text = isEnglish ? "🔑 Password:" : "🔑 Senha:", Foreground = Brushes.LightGray, FontSize = 14 });
                painelInfo.Children.Add(new TextBlock { Text = $"{carta.Password} (Custo: {carta.Starchip} ⭐)", Foreground = Brushes.Cyan, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 15) });
            }
            else
            {
                painelInfo.Children.Add(new TextBlock { Text = isEnglish ? "🚫 Password:" : "🚫 Senha:", Foreground = Brushes.LightGray, FontSize = 14 });
                painelInfo.Children.Add(new TextBlock { Text = isEnglish ? "Not Available" : "Não Disponível no Jogo", Foreground = Brushes.IndianRed, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 15) });
            }

            // --- FUSÕES ---
            var fusoesDestaCarta = HundoWindow.ListaFusoesGlobais.Where(f => f.Resultado == carta.Id).ToList();
            if (fusoesDestaCarta.Count > 0)
            {
                painelInfo.Children.Add(new TextBlock { Text = isEnglish ? "🔮 Fusions:" : "🔮 Fusões Possíveis:", Foreground = Brushes.Plum, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 5, 0, 10) });

                foreach (var f in fusoesDestaCarta)
                {
                    Border containerFusao = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(50, 221, 160, 221)),
                        Margin = new Thickness(0, 0, 0, 10),
                        Padding = new Thickness(10)
                    };

                    StackPanel stackFusao = new StackPanel { Orientation = Orientation.Vertical };
                    Grid gridCartas = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                    gridCartas.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    gridCartas.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
                    gridCartas.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var uiMat1 = CriarElementoClicavel(f.Mat1);
                    Grid.SetColumn(uiMat1, 0); gridCartas.Children.Add(uiMat1);

                    TextBlock txtPlus = new TextBlock { Text = "+", Foreground = Brushes.Gold, FontWeight = FontWeights.Bold, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(txtPlus, 1); gridCartas.Children.Add(txtPlus);

                    var uiMat2 = CriarElementoClicavel(f.Mat2);
                    Grid.SetColumn(uiMat2, 2); gridCartas.Children.Add(uiMat2);

                    stackFusao.Children.Add(gridCartas);

                    if (!string.IsNullOrWhiteSpace(f.Extra))
                    {
                        stackFusao.Children.Add(new TextBlock { Text = $"↪ {f.Extra}", Foreground = Brushes.LightGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
                    }

                    containerFusao.Child = stackFusao;
                    painelInfo.Children.Add(containerFusao);
                }
            }

            // --- RITUAIS ---
            var rituaisDestaCarta = HundoWindow.ListaRituaisGlobais.Where(r => r.Resultado == carta.Id).ToList();
            if (rituaisDestaCarta.Count > 0)
            {
                painelInfo.Children.Add(new TextBlock { Text = isEnglish ? "✡️ Rituals:" : "✡️ Rituais Possíveis:", Foreground = Brushes.LightSkyBlue, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 10) });

                foreach (var r in rituaisDestaCarta)
                {
                    Border containerRitual = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(50, 135, 206, 250)),
                        Margin = new Thickness(0, 0, 0, 10),
                        Padding = new Thickness(10)
                    };

                    StackPanel stackRitual = new StackPanel { Orientation = Orientation.Vertical };

                    Grid gridLinha1 = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                    gridLinha1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    gridLinha1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
                    gridLinha1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var uiT = CriarElementoClicavel(r.Mat1);
                    Grid.SetColumn(uiT, 0); gridLinha1.Children.Add(uiT);

                    TextBlock txtPlus1 = new TextBlock { Text = "+", Foreground = Brushes.Gold, FontWeight = FontWeights.Bold, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(txtPlus1, 1); gridLinha1.Children.Add(txtPlus1);

                    var uiU = CriarElementoClicavel(r.Mat2);
                    Grid.SetColumn(uiU, 2); gridLinha1.Children.Add(uiU);

                    stackRitual.Children.Add(gridLinha1);

                    Grid gridLinha2 = new Grid { Margin = new Thickness(0, 0, 0, 5) };
                    gridLinha2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    gridLinha2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
                    gridLinha2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var uiV = CriarElementoClicavel(r.Mat3);
                    Grid.SetColumn(uiV, 0); gridLinha2.Children.Add(uiV);

                    TextBlock txtPlus2 = new TextBlock { Text = "+", Foreground = Brushes.Gold, FontWeight = FontWeights.Bold, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(txtPlus2, 1); gridLinha2.Children.Add(txtPlus2);

                    var uiW = CriarElementoClicavel(r.Magica);
                    Grid.SetColumn(uiW, 2); gridLinha2.Children.Add(uiW);

                    stackRitual.Children.Add(gridLinha2);

                    containerRitual.Child = stackRitual;
                    painelInfo.Children.Add(containerRitual);
                }
            }
        }

        private Grid CriarElementoClicavel(string idCarta)
        {
            Grid grid = new Grid { Cursor = Cursors.Hand, Background = Brushes.Transparent };
            if (string.IsNullOrWhiteSpace(idCarta)) return grid;

            string nome = HundoWindow.MapaNomesCartas.ContainsKey(idCarta) ? HundoWindow.MapaNomesCartas[idCarta] : "???";

            // Lógica para verificar se o usuário possui ou não a carta
            bool possui = !HundoWindow.IdsCartasFaltantes.Contains(idCarta);

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Image img = CriarIconeCarta(idCarta);
            if (!possui) img.Opacity = 0.4; // Deixa a imagem escura se não possui a carta

            Grid.SetColumn(img, 0); grid.Children.Add(img);

            TextBlock txt = new TextBlock
            {
                Text = $"[{idCarta}] {nome}",
                Foreground = possui ? Brushes.White : Brushes.Gray, // Texto cinza se não possui
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 5, 0)
            };
            Grid.SetColumn(txt, 1); grid.Children.Add(txt);

            // Adiciona Tooltip para ajudar o usuário visualmente
            grid.ToolTip = possui ? (isEnglish ? "✅ Owned" : "✅ Você possui") : (isEnglish ? "❌ Missing" : "❌ Você não possui");

            grid.MouseLeftButtonUp += (s, e) => NavegarParaCarta(idCarta);
            return grid;
        }

        private void NavegarParaCarta(string idClicado)
        {
            if (HundoWindow.TodasAsCartasGlobais.ContainsKey(idClicado))
            {
                CarregarDadosCarta(HundoWindow.TodasAsCartasGlobais[idClicado]);
            }
        }

        private Image CriarIconeCarta(string idCarta)
        {
            Image img = new Image { Width = 35, Height = 35, Stretch = Stretch.Fill, VerticalAlignment = VerticalAlignment.Center };
            if (HundoWindow.MapaImagensGlobais.ContainsKey(idCarta))
            {
                try
                {
                    var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(HundoWindow.MapaImagensGlobais[idCarta], UriKind.Absolute);
                    bi.EndInit();
                    img.Source = bi;
                }
                catch { }
            }
            return img;
        }
    }

    // 4. O HundoWindow atualizado
    public class HundoWindow : Window
    {
        private WrapPanel painelCartas = null!;
        private TextBlock txtContador = null!;
        private TextBox txtBuscaNome = null!;
        private TextBox txtBuscaDrop = null!;
        private ComboBox cmbBuscaRank = null!;
        private CheckBox chkPassword = null!;
        private CheckBox chkFusions = null!;
        private CheckBox chkRituals = null!;
        private Image imgPersonagem = null!;
        private bool isEnglish;

        private List<CartaFaltante> todasFaltantes = new List<CartaFaltante>();

        public static List<FusaoInfo> ListaFusoesGlobais = new List<FusaoInfo>();
        public static List<RitualInfo> ListaRituaisGlobais = new List<RitualInfo>();
        public static Dictionary<string, string> MapaNomesCartas = new Dictionary<string, string>();
        public static Dictionary<string, string> MapaImagensGlobais = new Dictionary<string, string>();
        public static Dictionary<string, CartaFaltante> TodasAsCartasGlobais = new Dictionary<string, CartaFaltante>();
        public static HashSet<string> IdsCartasFaltantes = new HashSet<string>();

        public HundoWindow(bool isEng)
        {
            isEnglish = isEng;
            Title = isEnglish ? "Hundo Tracker (Missing Cards)" : "Tracker do Hundo (Cartas Faltantes)";

            // CORREÇÃO DE RESOLUÇÃO: Garante que a janela cabe no monitor do usuário e exibe todos os botões
            Width = SystemParameters.PrimaryScreenWidth < 1550 ? SystemParameters.PrimaryScreenWidth - 40 : 1500;
            Height = 600;

            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.SingleBorderWindow;

            DockPanel dock = new DockPanel();

            Border header = new Border { Background = Brushes.Black, Padding = new Thickness(10) };
            Grid headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }); // Coluna extra pro "?"

            txtContador = new TextBlock { Foreground = Brushes.Gold, FontSize = 18, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(txtContador, 0); headerGrid.Children.Add(txtContador);

            TextBlock lblCards = new TextBlock { Text = "Cards: ", Foreground = Brushes.White, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 5, 0) };
            Grid.SetColumn(lblCards, 1); headerGrid.Children.Add(lblCards);

            txtBuscaNome = new TextBox { VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(5), Margin = new Thickness(0, 0, 10, 0) };
            txtBuscaNome.TextChanged += (s, e) => FiltrarNaTela();
            Grid.SetColumn(txtBuscaNome, 2); headerGrid.Children.Add(txtBuscaNome);

            TextBlock lblDrop = new TextBlock { Text = "Drop From: ", Foreground = Brushes.White, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
            Grid.SetColumn(lblDrop, 3); headerGrid.Children.Add(lblDrop);

            txtBuscaDrop = new TextBox { VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(5), Margin = new Thickness(0, 0, 10, 0) };
            txtBuscaDrop.TextChanged += (s, e) => FiltrarNaTela();
            Grid.SetColumn(txtBuscaDrop, 4); headerGrid.Children.Add(txtBuscaDrop);

            TextBlock lblRank = new TextBlock { Text = "Rank: ", Foreground = Brushes.White, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
            Grid.SetColumn(lblRank, 5); headerGrid.Children.Add(lblRank);

            cmbBuscaRank = new ComboBox { VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(5), Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand };
            cmbBuscaRank.Items.Add(isEnglish ? "All" : "Todos");
            cmbBuscaRank.Items.Add("Pow");
            cmbBuscaRank.Items.Add("Tec");
            cmbBuscaRank.Items.Add("Bcd");
            cmbBuscaRank.SelectedIndex = 0;
            cmbBuscaRank.SelectionChanged += (s, e) => FiltrarNaTela();
            Grid.SetColumn(cmbBuscaRank, 6); headerGrid.Children.Add(cmbBuscaRank);

            chkPassword = new CheckBox { Content = "Password", Foreground = Brushes.Gold, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 10, 0), Cursor = Cursors.Hand };
            chkPassword.Checked += (s, e) => FiltrarNaTela();
            chkPassword.Unchecked += (s, e) => FiltrarNaTela();
            Grid.SetColumn(chkPassword, 7); headerGrid.Children.Add(chkPassword);

            chkFusions = new CheckBox { Content = isEnglish ? "Fusions" : "Fusões", Foreground = Brushes.Plum, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 10, 0), Cursor = Cursors.Hand };
            chkFusions.Checked += (s, e) => FiltrarNaTela();
            chkFusions.Unchecked += (s, e) => FiltrarNaTela();
            Grid.SetColumn(chkFusions, 8); headerGrid.Children.Add(chkFusions);

            chkRituals = new CheckBox { Content = isEnglish ? "Rituals" : "Rituais", Foreground = Brushes.LightSkyBlue, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 15, 0), Cursor = Cursors.Hand };
            chkRituals.Checked += (s, e) => FiltrarNaTela();
            chkRituals.Unchecked += (s, e) => FiltrarNaTela();
            Grid.SetColumn(chkRituals, 9); headerGrid.Children.Add(chkRituals);

            Button btnFarm = new Button { Content = isEnglish ? "💡 Best Farm" : "💡 Melhor Farm", Background = Brushes.Orange, Foreground = Brushes.Black, FontWeight = FontWeights.Bold, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand };
            btnFarm.Click += (s, e) => {
                MelhorFarmWindow janelaFarm = new MelhorFarmWindow(this, todasFaltantes, isEnglish);
                janelaFarm.ShowDialog();
            };
            Grid.SetColumn(btnFarm, 10); headerGrid.Children.Add(btnFarm);

            Button btnSync = new Button { Content = "🔄 SYNC", Background = Brushes.DarkCyan, Foreground = Brushes.White, FontWeight = FontWeights.Bold, Padding = new Thickness(15, 5, 15, 5), Margin = new Thickness(0, 0, 10, 0), Cursor = Cursors.Hand };
            btnSync.Click += (s, e) => CarregarCartasFaltantesCSV();
            Grid.SetColumn(btnSync, 11); headerGrid.Children.Add(btnSync);

            // NOVO BOTÃO: "?" (Ajuda)
            Button btnAjuda = new Button { Content = "❓", Background = Brushes.DimGray, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 16, Padding = new Thickness(10, 0, 10, 0), Cursor = Cursors.Hand };
            btnAjuda.Click += (s, e) => {
                SobreWindow janelaSobre = new SobreWindow(isEnglish);
                janelaSobre.ShowDialog();
            };
            Grid.SetColumn(btnAjuda, 12); headerGrid.Children.Add(btnAjuda);

            header.Child = headerGrid;
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            Grid areaPrincipal = new Grid();

            painelCartas = new WrapPanel { Margin = new Thickness(10) };
            ScrollViewer scroll = new ScrollViewer { Content = painelCartas, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            areaPrincipal.Children.Add(scroll);

            imgPersonagem = new Image
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Width = 180,
                Height = 250,
                Margin = new Thickness(10, 0, 0, 10),
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
                Opacity = 0.95
            };
            areaPrincipal.Children.Add(imgPersonagem);

            dock.Children.Add(areaPrincipal);
            Content = dock;

            CarregarCartasFaltantesCSV();
        }

        // NOVO MÉTODO: Permite que a janela de ranking aplique o filtro de Drop automaticamente
        public void AplicarFiltroDrop(string nomePersonagem)
        {
            txtBuscaDrop.Text = nomePersonagem; // Isso dispara o TextChanged e filtra sozinho!
        }

        private void CarregarCartasFaltantesCSV()
        {
            todasFaltantes.Clear();
            IdsCartasFaltantes.Clear();
            ListaFusoesGlobais.Clear();
            ListaRituaisGlobais.Clear();
            MapaNomesCartas.Clear();
            MapaImagensGlobais.Clear();
            TodasAsCartasGlobais.Clear();

            string caminhoArquivo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Master_FM.csv");
            if (!File.Exists(caminhoArquivo))
            {
                txtContador.Text = isEnglish ? "Master_FM.csv not found!" : "Master_FM.csv não encontrado!";
                return;
            }

            var mapaImagens = new Dictionary<string, string>();
            string pastaIcons = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons");
            if (Directory.Exists(pastaIcons))
            {
                foreach (var arq in Directory.GetFiles(pastaIcons))
                {
                    string nomeArquivo = Path.GetFileName(arq);
                    string idStr = new string(nomeArquivo.TakeWhile(char.IsDigit).ToArray());
                    if (int.TryParse(idStr, out int idInt))
                    {
                        mapaImagens[idInt.ToString()] = arq;
                        MapaImagensGlobais[idInt.ToString()] = arq;
                    }
                }
            }

            List<string> linhas = new List<string>();
            try
            {
                using (var fs = new FileStream(caminhoArquivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string? linha;
                    while ((linha = sr.ReadLine()) != null) linhas.Add(linha);
                }
            }
            catch { return; }

            if (linhas.Count == 0) return;

            char separador = linhas[0].Contains(";") ? ';' : ',';

            for (int i = 1; i < linhas.Count; i++)
            {
                var colunas = linhas[i].Split(separador);

                string idCsv = colunas[0].Trim().Replace("\"", "");
                string nomeCsv = colunas.Length > 1 ? colunas[1].Trim().Replace("\"", "") : "";

                if (!string.IsNullOrEmpty(idCsv) && !MapaNomesCartas.ContainsKey(idCsv))
                {
                    MapaNomesCartas[idCsv] = nomeCsv;
                }

                string mat1 = colunas.Length > 15 ? colunas[15].Trim().Replace("\"", "") : "";
                string mat2 = colunas.Length > 16 ? colunas[16].Trim().Replace("\"", "") : "";
                string resultadoFusao = colunas.Length > 17 ? colunas[17].Trim().Replace("\"", "") : "";
                string extraFusao = colunas.Length > 18 ? colunas[18].Trim().Replace("\"", "") : "";

                if (!string.IsNullOrEmpty(resultadoFusao) && !string.IsNullOrEmpty(mat1) && !string.IsNullOrEmpty(mat2))
                {
                    ListaFusoesGlobais.Add(new FusaoInfo { Mat1 = mat1, Mat2 = mat2, Resultado = resultadoFusao, Extra = extraFusao });
                }

                string ritMat1 = colunas.Length > 19 ? colunas[19].Trim().Replace("\"", "") : "";
                string ritMat2 = colunas.Length > 20 ? colunas[20].Trim().Replace("\"", "") : "";
                string ritMat3 = colunas.Length > 21 ? colunas[21].Trim().Replace("\"", "") : "";
                string ritMagica = colunas.Length > 22 ? colunas[22].Trim().Replace("\"", "") : "";
                string ritResult = colunas.Length > 23 ? colunas[23].Trim().Replace("\"", "") : "";

                if (!string.IsNullOrEmpty(ritResult) && (!string.IsNullOrEmpty(ritMagica) || !string.IsNullOrEmpty(ritMat1)))
                {
                    ListaRituaisGlobais.Add(new RitualInfo { Mat1 = ritMat1, Mat2 = ritMat2, Mat3 = ritMat3, Magica = ritMagica, Resultado = ritResult });
                }

                if (int.TryParse(idCsv, out int idInt))
                {
                    string idBuscado = idInt.ToString();

                    string passwordCsv = colunas.Length > 2 ? colunas[2].Trim().Replace("\"", "") : "";
                    string starchipCsv = colunas.Length > 3 ? colunas[3].Trim().Replace("\"", "") : "";
                    string disponibilidadeCsv = colunas.Length > 4 ? colunas[4].Trim().Replace("\"", "").ToUpper() : "";
                    string dropCsv = colunas.Length > 12 ? colunas[12].Trim().Replace("\"", "") : "";
                    string rankCsv = colunas.Length > 13 ? colunas[13].Trim().Replace("\"", "") : "";
                    string caminhoImg = mapaImagens.ContainsKey(idBuscado) ? mapaImagens[idBuscado] : "";

                    var cartaObj = new CartaFaltante
                    {
                        Id = idBuscado,
                        Nome = nomeCsv,
                        CaminhoImagem = caminhoImg,
                        DropFrom = dropCsv,
                        Rank = rankCsv,
                        Password = passwordCsv,
                        Starchip = starchipCsv,
                        Disponibilidade = disponibilidadeCsv
                    };

                    TodasAsCartasGlobais[idBuscado] = cartaObj;

                    string owned = colunas.Length > 5 ? colunas[5].Trim().Replace("\"", "").ToUpper() : "";

                    if (owned != "Y")
                    {
                        IdsCartasFaltantes.Add(idBuscado);
                        if (!string.IsNullOrEmpty(caminhoImg))
                        {
                            todasFaltantes.Add(cartaObj);
                        }
                    }
                }
            }

            FiltrarNaTela();
        }

        // 5. NOVA JANELA: Ranking de Melhor Farm
        public class MelhorFarmWindow : Window
        {
            public MelhorFarmWindow(HundoWindow mainWindow, List<CartaFaltante> faltantes, bool isEnglish)
            {
                Title = isEnglish ? "Best Farm Route" : "Melhor Rota de Farm";
                Width = 450; Height = 600;
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                WindowStyle = WindowStyle.ToolWindow;

                Grid principal = new Grid { Margin = new Thickness(15) };
                principal.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                principal.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                TextBlock titulo = new TextBlock
                {
                    Text = isEnglish ? "🏆 Top Farmers (Missing Cards)" : "🏆 Top Drops (Cartas Faltantes)",
                    Foreground = Brushes.Gold,
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 15)
                };
                Grid.SetRow(titulo, 0);
                principal.Children.Add(titulo);

                // Lógica de agrupamento: Conta quantas cartas faltam de cada personagem
                var ranking = faltantes
                    .Where(c => !string.IsNullOrWhiteSpace(c.DropFrom) && c.DropFrom != "-")
                    .GroupBy(c => c.DropFrom)
                    .Select(g => new { Personagem = g.Key, Quantidade = g.Count() })
                    .OrderByDescending(x => x.Quantidade)
                    .ToList();

                ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                StackPanel painelRanking = new StackPanel();

                if (ranking.Count == 0)
                {
                    painelRanking.Children.Add(new TextBlock
                    {
                        Text = isEnglish ? "No drops left to farm!" : "Nenhum farm restante!",
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 20, 0, 0)
                    });
                }
                else
                {
                    int posicao = 1;
                    foreach (var item in ranking)
                    {
                        Border card = new Border
                        {
                            Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                            BorderBrush = posicao <= 3 ? Brushes.Gold : Brushes.DimGray, // Top 3 ganha borda dourada
                            BorderThickness = new Thickness(2),
                            Margin = new Thickness(0, 0, 0, 10),
                            Padding = new Thickness(10),
                            CornerRadius = new CornerRadius(5)
                        };

                        Grid gridCard = new Grid();
                        gridCard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); // Posição
                        gridCard.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Nome
                        gridCard.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Quantidade
                        gridCard.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Botão Filtrar

                        // Posição
                        TextBlock txtPos = new TextBlock { Text = $"#{posicao}", Foreground = posicao <= 3 ? Brushes.Gold : Brushes.Gray, FontSize = 18, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                        Grid.SetColumn(txtPos, 0); gridCard.Children.Add(txtPos);

                        // Nome do Personagem
                        TextBlock txtNome = new TextBlock { Text = item.Personagem, Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
                        Grid.SetColumn(txtNome, 1); gridCard.Children.Add(txtNome);

                        // Quantidade de Cartas
                        TextBlock txtQtd = new TextBlock { Text = $"{item.Quantidade} cartas", Foreground = Brushes.LimeGreen, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 15, 0) };
                        Grid.SetColumn(txtQtd, 2); gridCard.Children.Add(txtQtd);

                        // Botão para aplicar o filtro na janela principal
                        Button btnFiltrar = new Button
                        {
                            Content = isEnglish ? "Filter" : "Filtrar",
                            Background = Brushes.DarkSlateBlue,
                            Foreground = Brushes.White,
                            FontWeight = FontWeights.Bold,
                            Padding = new Thickness(10, 5, 10, 5),
                            Cursor = Cursors.Hand
                        };
                        btnFiltrar.Click += (s, e) => {
                            mainWindow.AplicarFiltroDrop(item.Personagem);
                            this.Close(); // Fecha a janela de ranking após aplicar o filtro
                        };
                        Grid.SetColumn(btnFiltrar, 3); gridCard.Children.Add(btnFiltrar);

                        card.Child = gridCard;
                        painelRanking.Children.Add(card);
                        posicao++;
                    }
                }

                scroll.Content = painelRanking;
                Grid.SetRow(scroll, 1);
                principal.Children.Add(scroll);

                Content = principal;
            }
        }

        private void FiltrarNaTela()
        {
            painelCartas.Children.Clear();
            string buscaNome = txtBuscaNome.Text.Trim().ToLower();
            string buscaDrop = txtBuscaDrop.Text.Trim().ToLower();
            string buscaRank = cmbBuscaRank.SelectedItem?.ToString() ?? "";

            bool apenasPassword = chkPassword.IsChecked == true;
            bool apenasFusoes = chkFusions.IsChecked == true;
            bool apenasRituais = chkRituals.IsChecked == true;

            bool ignorarRank = buscaRank == "Todos" || buscaRank == "All";

            var cartasFiltradas = todasFaltantes.Where(c =>
                (string.IsNullOrEmpty(buscaNome) || c.Nome.ToLower().Contains(buscaNome) || c.Id == buscaNome) &&
                (string.IsNullOrEmpty(buscaDrop) || c.DropFrom.ToLower().Contains(buscaDrop)) &&
                (ignorarRank || c.Rank.ToLower().Contains(buscaRank.ToLower())) &&
                (!apenasPassword || (c.Disponibilidade != "N" && !string.IsNullOrEmpty(c.Password))) &&
                (!apenasFusoes || ListaFusoesGlobais.Any(f => f.Resultado == c.Id)) &&
                (!apenasRituais || ListaRituaisGlobais.Any(r => r.Resultado == c.Id))
            ).ToList();

            foreach (var carta in cartasFiltradas)
            {
                CriarElementoCarta(carta);
            }

            txtContador.Text = isEnglish ? $"Missing: {todasFaltantes.Count} cards" : $"Faltam: {todasFaltantes.Count} cartas";

            imgPersonagem.Source = null;

            if (!string.IsNullOrEmpty(buscaDrop))
            {
                string pastaPersonagens = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Personagens");

                if (!Directory.Exists(pastaPersonagens)) Directory.CreateDirectory(pastaPersonagens);

                var arquivosPersonagens = Directory.GetFiles(pastaPersonagens);
                var arquivoEncontrado = arquivosPersonagens.FirstOrDefault(a =>
                    Path.GetFileNameWithoutExtension(a).ToLower().Contains(buscaDrop));

                if (arquivoEncontrado != null)
                {
                    try
                    {
                        var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(arquivoEncontrado, UriKind.Absolute); bi.EndInit();
                        imgPersonagem.Source = bi;
                    }
                    catch { }
                }
            }
        }

        // 6. NOVA JANELA: Informações, Como Usar e Créditos (PIX no topo blindado)
        public class SobreWindow : Window
        {
            public SobreWindow(bool isEnglish)
            {
                Title = isEnglish ? "About YugiohTracker" : "Sobre o YugiohTracker";
                Width = 500; Height = 580;
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30));
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                WindowStyle = WindowStyle.ToolWindow;
                ResizeMode = ResizeMode.NoResize;

                ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(15) };
                StackPanel painel = new StackPanel();

                // Título
                painel.Children.Add(new TextBlock
                {
                    Text = "ℹ️ Sobre o YugiohTracker (Hundo)",
                    Foreground = Brushes.Gold,
                    FontSize = 20,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 15)
                });

                // --- ÁREA DO PIX MOVIDA PARA O INÍCIO ---
                Border areaPix = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
                    BorderBrush = Brushes.LimeGreen,
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(10),
                    Margin = new Thickness(0, 0, 0, 20)
                };
                StackPanel stackPix = new StackPanel();
                stackPix.Children.Add(new TextBlock
                {
                    Text = isEnglish ? "Want to support the project? Donate via PIX:" : "Gostou do aplicativo? Apoie o projeto via PIX:",
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 10)
                });

                string chavePix = "fcb2fa70-22d3-4ac5-8d1e-cf1a4c470c38";

                Grid gridPix = new Grid();
                gridPix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                gridPix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                TextBox txtPix = new TextBox
                {
                    Text = chavePix,
                    IsReadOnly = true,
                    Background = Brushes.Black,
                    Foreground = Brushes.LimeGreen,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(5),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(txtPix, 0); gridPix.Children.Add(txtPix);

                Button btnCopiar = new Button
                {
                    Content = isEnglish ? "📋 Copy" : "📋 Copiar",
                    Background = Brushes.DarkSlateGray,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(10, 0, 0, 0),
                    Cursor = Cursors.Hand
                };

                // CORREÇÃO: Try/Catch e IsLoaded para evitar os crashes
                btnCopiar.Click += (s, e) => {
                    try
                    {
                        Clipboard.SetText(chavePix);
                        btnCopiar.Content = isEnglish ? "✅ Copied!" : "✅ Copiado!";

                        Task.Delay(2000).ContinueWith(t => {
                            try
                            {
                                Dispatcher.Invoke(() => {
                                    if (this.IsLoaded)
                                    {
                                        btnCopiar.Content = isEnglish ? "📋 Copy" : "📋 Copiar";
                                    }
                                });
                            }
                            catch { }
                        });
                    }
                    catch
                    {
                        btnCopiar.Content = "❌ Erro";
                    }
                };

                Grid.SetColumn(btnCopiar, 1); gridPix.Children.Add(btnCopiar);

                areaPix.Child = stackPix;
                stackPix.Children.Add(gridPix);
                painel.Children.Add(areaPix);
                // ----------------------------------------

                // Como Usar
                painel.Children.Add(new TextBlock { Text = isEnglish ? "📖 How to use:" : "📖 Como Usar:", Foreground = Brushes.LightSkyBlue, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });
                painel.Children.Add(CriarTextoBalao(isEnglish ? "• Left Click: Marks the card as obtained (saves to Excel and removes from screen)." : "• Botão Esquerdo: Marca a carta como obtida (salva no Excel e remove da tela)."));
                painel.Children.Add(CriarTextoBalao(isEnglish ? "• Right Click: Opens Card Details, showing Fusions, Rituals and Drop locations." : "• Botão Direito: Abre a janela de Detalhes da Carta, mostrando Fusões, Rituais e locais de Drop."));
                painel.Children.Add(CriarTextoBalao(isEnglish ? "• Wiki Navigation: Inside details, click any fusion/ritual material card to jump to its info." : "• Navegação Wiki: Dentro dos detalhes, clique em qualquer carta material de fusão ou ritual para pular direto para as informações dela."));

                // Legenda Visual
                painel.Children.Add(new TextBlock { Text = isEnglish ? "🎨 Visual Legend:" : "🎨 Legenda Visual:", Foreground = Brushes.Plum, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 15, 0, 10) });
                painel.Children.Add(CriarTextoBalao(isEnglish ? "• Dark Images (in Details): Indicates you don't own the card yet." : "• Imagens Escurecidas (nos Detalhes): Indica que você ainda não possui aquela carta na sua coleção."));
                painel.Children.Add(CriarTextoBalao(isEnglish ? "• Clear Images (in Details): You own the card and can use it as material." : "• Imagens Claras (nos Detalhes): Você já possui essa carta e pode usá-la como material."));
                painel.Children.Add(CriarTextoBalao(isEnglish ? "• Green Flash: Quick visual confirmation that the card was successfully registered." : "• Fundo Verde: Confirmação visual rápida de que a carta foi registrada com sucesso."));

                // Linha Divisória
                painel.Children.Add(new Border { Height = 1, Background = Brushes.DimGray, Margin = new Thickness(0, 20, 0, 20) });

                // Créditos
                painel.Children.Add(new TextBlock { Text = isEnglish ? "🏆 Credits:" : "🏆 Créditos:", Foreground = Brushes.Gold, FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });
                painel.Children.Add(CriarTextoBalao("Desenvolvido por: Pedro Henrique Guerdis Silva"));
                painel.Children.Add(CriarTextoBalao("Base de Dados: Informações extraídas via Master_FM.csv"));

                scroll.Content = painel;
                Content = scroll;
            }

            private TextBlock CriarTextoBalao(string texto)
            {
                return new TextBlock
                {
                    Text = texto,
                    Foreground = Brushes.LightGray,
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8)
                };
            }
        }

        private void CriarElementoCarta(CartaFaltante carta)
        {
            Grid container = new Grid { Width = 70, Height = 70, Margin = new Thickness(5), Cursor = Cursors.Hand };

            string infoTooltip = $"[{carta.Id}] {carta.Nome}\nDrop From: {carta.DropFrom}\nRank: {carta.Rank}";
            if (carta.Disponibilidade != "N" && !string.IsNullOrEmpty(carta.Password))
                infoTooltip += $"\nPassword: {carta.Password} (Custo: {carta.Starchip}⭐)";
            if (ListaFusoesGlobais.Any(f => f.Resultado == carta.Id))
                infoTooltip += "\n🔮 Fusionável";
            if (ListaRituaisGlobais.Any(r => r.Resultado == carta.Id))
                infoTooltip += "\n✡️ Ritual";

            container.ToolTip = new ToolTip { Content = infoTooltip };

            Image img = new Image { Stretch = Stretch.Fill };
            try
            {
                var bi = new BitmapImage(); bi.BeginInit(); bi.CacheOption = BitmapCacheOption.OnLoad; bi.UriSource = new Uri(carta.CaminhoImagem, UriKind.Absolute); bi.EndInit();
                img.Source = bi;
            }
            catch { return; }
            container.Children.Add(img);

            Border bordaDourada = new Border { BorderBrush = Brushes.Gold, BorderThickness = new Thickness(2), IsHitTestVisible = false };
            container.Children.Add(bordaDourada);

            Border flashVerde = new Border { Background = Brushes.LimeGreen, Opacity = 0, IsHitTestVisible = false };
            container.Children.Add(flashVerde);

            container.MouseLeftButtonUp += (s, e) =>
            {
                MainWindow.SalvarCartaNoExcel(carta.Id);

                Task.Run(async () => {
                    await Dispatcher.InvokeAsync(() => flashVerde.Opacity = 0.6);
                    await Task.Delay(150);
                    await Dispatcher.InvokeAsync(() => {
                        todasFaltantes.Remove(carta);
                        IdsCartasFaltantes.Remove(carta.Id);
                        FiltrarNaTela();
                    });
                });
            };

            container.MouseRightButtonUp += (s, e) =>
            {
                DetalhesCartaWindow detalhes = new DetalhesCartaWindow(carta, isEnglish);
                detalhes.ShowDialog();
            };

            painelCartas.Children.Add(container);
        }
    }
}